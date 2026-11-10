using System.Diagnostics;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// A secret that a terminal or a text box wrapped onto the next row (ST-042, the third weak spot after
/// #268). The page text the detectors read joins OCR words with spaces, so a key cut in two by the
/// window's right edge is two words to them: the first row's piece is masked when it is long enough to
/// match by itself, the piece on the next row never, and a key whose first piece is too short to match
/// is stored whole. These lay out pages the way a wrapped line looks: the first row runs to the right
/// edge, the rest starts at or near the left edge on the row below.
/// </summary>
public sealed class OcrWrapTests
{
    private const int Height = 20;
    private const int Glyph = 10;
    private const int Space = 10; // half the row's height: a real space, wider than the token rule joins
    private const int Pitch = 30;
    private const int Left = 40;

    private static readonly RedactionEngine Engine = new();
    private static readonly RedactionEngine WithEmails = new(new RedactionPolicy { Emails = true });

    [Fact]
    public void AKeyWrappedWithNoHyphenIsMaskedOnBothRows()
    {
        // Ten characters after the prefix on the first row, where the rule wants sixteen: neither piece
        // matches by itself, and before the wrap rule the whole key was stored.
        var words = Page(
            Row(0, Left, "deploy", "--key", "sk_live_ABCDEFGHIJ"),
            Row(1, Left, "KLMNOPQRSTUV0123", "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("deploy --key [SECRET] --region eu", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the key's first row is left unpainted in the frame");
        Assert.True(Covered(redaction, words[3]), "the key's second row is left unpainted in the frame");
        Assert.False(Touched(redaction, words[1]), "a word a space before the key is painted");
        Assert.False(Touched(redaction, words[4]), "a word a space after the key is painted");
        Assert.Equal(1, redaction.Counts[MaskKind.ApiKey]);
    }

    [Fact]
    public void ABase64KeyWrappedInTheMiddleIsMaskedOnBothRows()
    {
        // The first row's piece is long enough to match alone, so it was masked and the rest of the key
        // stayed on the row below, in the text and in the picture.
        var words = Page(
            Row(0, Left, "token:", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo+/a"),
            Row(1, Left, "bGciOiJIUzI1NiJ9+U1RVVldYWVo=", "saved"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("token: [SECRET] saved", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the key's second row is left unpainted in the frame");
        Assert.Equal(1, redaction.Counts[MaskKind.ApiKey]);
    }

    [Fact]
    public void AKeyWrappedOverThreeRowsIsMaskedOnAll()
    {
        // A signed token's three parts, each cut by the window: no row holds both full stops.
        var words = Page(
            Row(0, Left, "auth", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIx"),
            Row(1, Left, "MjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2Mj"),
            Row(2, Left, "M5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c", "ok"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("auth [SECRET] ok", redaction.Text);
        Assert.All(Enumerable.Range(1, 3), i => Assert.True(Covered(redaction, words[i]), $"piece {i} is left unpainted in the frame"));
    }

    [Fact]
    public void AnIndentedContinuationIsMaskedToo()
    {
        // An editor that indents the rest of a wrapped line under its first.
        var words = Page(
            Row(0, Left, "deploy", "--key", "sk_live_ABCDEFGHIJ"),
            Row(1, Left + (4 * Glyph), "KLMNOPQRSTUV0123", "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("deploy --key [SECRET] --region eu", redaction.Text);
        Assert.True(Covered(redaction, words[3]), "the indented piece is left unpainted in the frame");
    }

    [Fact]
    public void AContinuationWithARowOfAnotherColumnBetweenIsMaskedToo()
    {
        // Two panes whose lines do not sit level: the next row down the page belongs to the other pane.
        var words = Page(
            Row(0, 500, "deploy", "--key", "sk_live_ABCDEFGHIJ"),
            [new OcrWord("Notes", 20, 16, 50, Height), new OcrWord("pane", 80, 16, 40, Height)],
            Row(1, 500, "KLMNOPQRSTUV0123", "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("deploy --key [SECRET] Notes pane [SECRET] --region eu", redaction.Text);
        Assert.True(Covered(redaction, words[5]), "the key's second row is left unpainted in the frame");
        Assert.False(Touched(redaction, words[3]), "the other pane is painted");
    }

    [Fact]
    public void AWrapInTheRightHandPaneIsMaskedAndTheLeftPaneIsNot()
    {
        // Side by side on the same rows, so neither piece is the first or last word of its row.
        var words = Page(
            [.. Row(0, 20, "Notes", "pane"), .. Row(0, 500, "deploy", "--key", "sk_live_ABCDEFGHIJ")],
            [.. Row(1, 20, "second", "note"), .. Row(1, 500, "KLMNOPQRSTUV0123", "--region", "eu")]);

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("Notes pane deploy --key [SECRET] second note [SECRET] --region eu", redaction.Text);
        Assert.True(Covered(redaction, words[4]), "the key's first row is left unpainted in the frame");
        Assert.True(Covered(redaction, words[7]), "the key's second row is left unpainted in the frame");
        Assert.False(Touched(redaction, words[5]), "the left pane is painted");
    }

    [Fact]
    public void AWrappedKeyTheEngineAlsoSplitIsMaskedWhole()
    {
        // The first row's piece read as two words with no space between them on the screen.
        var first = Row(0, Left, "deploy", "--key", "sk_live_ABCDE");
        var words = Page(
            [.. first, new OcrWord("FGHIJ", first[^1].X + first[^1].Width + 3, 0, 5 * Glyph, Height)],
            Row(1, Left, "KLMNOPQRSTUV0123", "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("deploy --key [SECRET] --region eu", redaction.Text);
        Assert.All(Enumerable.Range(2, 3), i => Assert.True(Covered(redaction, words[i]), $"piece {i} is left unpainted in the frame"));
    }

    [Fact]
    public void AWrappedEmailIsMaskedOnBothRows()
    {
        var words = Page(
            Row(0, Left, "send", "the", "report", "to", "reception.frontdesk@exam"),
            Row(1, Left, "ple.com", "today"));

        var redaction = WithEmails.RedactFrame(words);

        Assert.Equal("send the report to [EMAIL] today", redaction.Text);
    }

    [Fact]
    public void ACardNumberCutInsideAGroupIsMaskedOnBothRows()
    {
        var words = Page(
            Row(0, Left, "card", "4111", "1111", "11"),
            Row(1, Left, "11", "1111", "ok"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("card [CARD] ok", redaction.Text);
    }

    [Fact]
    public void TwoUnrelatedLinesAreLeftAlone()
    {
        // The same shape as a wrap, a full row over a row from the left edge, and nothing in it that is
        // a secret once joined.
        var words = Page(
            Row(0, Left, "Queue", "cleared", "and", "a", "test", "page", "sent"),
            Row(1, Left, "Ticket", "48213", "reception", "printer", "offline"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("Queue cleared and a test page sent Ticket 48213 reception printer offline", redaction.Text);
        Assert.Empty(redaction.Regions);
    }

    [Fact]
    public void ARowBelowThatRunsPastTheRowAboveIsNotItsContinuation()
    {
        // A line that overflowed is as wide as its box, so what continues it cannot run on past it. The
        // two numbers joined would pass the card checksum.
        var words = Page(
            Row(0, Left, "order", "4111111111"),
            Row(1, Left, "111111", "widgets", "shipped", "to", "the", "warehouse"));

        var redaction = Engine.RedactFrame(words);

        Assert.Empty(redaction.Regions);
    }

    [Fact]
    public void TwoStackedNumbersThatTogetherPassTheChecksumAreMasked()
    {
        // The rule's cost, stated: it cannot tell these from a card number cut by the window's edge.
        var words = Page(
            Row(0, Left, "order", "4111111111"),
            Row(1, Left, "111111", "units"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("order [CARD] units", redaction.Text);
    }

    [Fact]
    public void ARowTooFarBelowIsNotAContinuation()
    {
        // Three blank lines between them.
        var words = Page(
            Row(0, Left, "deploy", "--key", "sk_live_ABCDEFGHIJ"),
            Row(4, Left, "KLMNOPQRSTUV0123", "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        Assert.Empty(redaction.Regions);
    }

    [Fact]
    public void AWrapOnARightToLeftRowIsReadBothWays()
    {
        // A Latin key in a Hebrew line: the line ends at its left edge, and goes on from the right edge
        // of the row below.
        OcrWord[] words =
        [
            new("sk_live_ABCDEFGHIJ", Left, 0, 180, Height),
            new("מפתח", 240, 0, 60, Height),
            new("הפעלה", 310, 0, 70, Height),
            new("סיום", 150, Pitch, 50, Height),
            new("KLMNOPQRSTUV0123", 220, Pitch, 160, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("sk_live", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("KLMNOP", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[0]) && Covered(redaction, words[4]), "a piece of the key is left unpainted in the frame");
    }

    [Fact]
    public void AWrapOnARightToLeftRowIsStillReadLeftToRight()
    {
        // The engine may have a right-to-left letter on a row that reads left to right all the same.
        var words = Page(
            Row(0, Left, "מפתח", "--key", "sk_live_ABCDEFGHIJ"),
            Row(1, Left, "KLMNOPQRSTUV0123", "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("sk_live", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("KLMNOP", redaction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrappedPasswordTakesBothRows()
    {
        // The password rule's value ends where the first row does. Joined, the match touches both rows,
        // and a password takes its rows whole: the cue's from the cue on, the row below all of it.
        var words = Page(
            Row(0, Left, "wifi", "password:", "Tangerine4821Wob"),
            Row(1, Left, "ble", "is", "on", "the", "router"),
            Row(2, Left, "Queue", "cleared"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("wifi [REDACTED] Queue cleared", redaction.Text);
        Assert.All(Enumerable.Range(1, 7), i => Assert.True(Covered(redaction, words[i]), $"word {i} is left unpainted in the frame"));
        Assert.False(Touched(redaction, words[0]), "the word left of the cue is painted");
        Assert.False(Touched(redaction, words[8]), "the row after the password's rows is painted");
        Assert.Equal(1, redaction.Counts[MaskKind.Password]);
    }

    [Fact]
    public void AWrappedConnectionStringsPasswordTakesBothRows()
    {
        var words = Page(
            Row(0, Left, "Server=db01;User", "Id=sa;Password=Tangerine48"),
            Row(1, Left, "21Wobble;Encrypt=true", "saved"));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("Tangerine", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Wobble", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[2]), "the password's second row is left unpainted in the frame");
        Assert.True(Covered(redaction, words[3]), "a password's row is masked whole");
    }

    [Fact]
    public void APasswordAtTheEndOfItsRowTakesAShortRowBelow()
    {
        // The rule's cost, stated: a password that ends its row over a row no wider reads the same as
        // one that wrapped, and a password takes every row its match touches.
        var words = Page(
            Row(0, Left, "wifi", "password:", "Tangerine4821"),
            Row(1, Left, "Queue", "cleared"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("wifi [REDACTED]", redaction.Text);
    }

    [Fact]
    public void AWrappedPasswordOnARightToLeftRowTakesBothRowsWhole()
    {
        var words = Page(
            Row(0, Left, "סיסמה", "password:", "Tangerine4821Wob"),
            Row(1, Left, "ble", "נתב"));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("[REDACTED]", redaction.Text);
    }

    [Fact]
    public void AWrapIsFoundHoweverTheEngineListsThePage()
    {
        // The joins are made on the screen, so the order of the list changes none of them. (What the
        // page's own text matches does follow the list, as it always has.)
        var words = Page(
            Row(0, Left, "deploy", "--key", "sk_live_ABCDEFGHIJ"),
            Row(1, Left, "KLMNOPQRSTUV0123", "--region", "eu"),
            Row(2, Left, "wifi", "password:", "Tangerine4821Wob"),
            Row(3, Left, "ble", "is", "on"));
        string[] expected = ["sk_live_ABCDEFGHIJ", "KLMNOPQRSTUV0123", "Tangerine4821Wob", "ble", "is", "on"];

        var random = new Random(269);
        for (var pass = 0; pass < 50; pass++)
        {
            var shuffled = words.OrderBy(_ => random.Next()).ToArray();

            var redaction = Engine.RedactFrame(shuffled);

            var masked = MaskedTexts(redaction, shuffled);
            Assert.All(expected, piece => Assert.Contains(piece, masked));
            Assert.All(expected, piece => Assert.DoesNotContain($" {piece} ", $" {redaction.Text} ", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The wrap pass reads the page a second time, so it may cost as much again as the first and no
    /// more. Every row of these is a full row over a row from the left edge, the shape it looks for, and
    /// the grid is the worst of them: every cell is a row end and a row start at once.
    /// </summary>
    [Theory]
    [InlineData("8,000 words, every row a wrap of the one above")]
    [InlineData("5,000 cells, every one a column of its own")]
    [InlineData("5,000 words one to a row")]
    public void ABigFrameOfWrapsRedactsWithinBudget(string frame)
    {
        var words = frame switch
        {
            "8,000 words, every row a wrap of the one above" => Grid(8000, 40, 50, (row, col) => col == 39 ? $"sk_live_AB{row}" : col == 0 ? $"CD{row}EF" : $"w{row}x{col}"),
            "5,000 cells, every one a column of its own" => Grid(5000, 20, 200, (row, col) => $"{row * 37 % 10}{((row * 20) + col) % 100000:00000}"),
            _ => Grid(5000, 1, 50, (row, _) => $"{row % 10}{row % 100000:00000}"),
        };

        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var clock = Stopwatch.StartNew();
            _ = Engine.RedactFrame(words);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        Assert.True(best < 700, $"{frame}: {best:0} ms, over the 700 ms a frame may take");
    }

    /// <summary>Twice the words may cost about twice the time, never four times.</summary>
    [Fact]
    public void TheWrapPassGrowsWithThePageNotItsSquare()
    {
        static double Best(List<OcrWord> words)
        {
            var best = double.MaxValue;
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                _ = Engine.RedactFrame(words);
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        // Every cell a column of its own, one above the other: the longest chains of rows there are.
        var small = Grid(4000, 4, 200, (row, col) => $"{row % 10}{((row * 4) + col) % 100000:00000}");
        var large = Grid(16000, 4, 200, (row, col) => $"{row % 10}{((row * 4) + col) % 100000:00000}");
        _ = Best(small);

        var (few, many) = (Best(small), Best(large));

        Assert.True(many < Math.Max(40, few * 10), $"4,000 words took {few:0.0} ms and 16,000 took {many:0.0} ms: four times the words cost more than ten times the time");
    }

    private static List<OcrWord> Grid(int count, int columns, int pitch, Func<int, int, string> text)
    {
        var words = new List<OcrWord>(count);
        for (var i = 0; i < count; i++)
        {
            var (row, col) = (i / columns, i % columns);
            words.Add(new(text(row, col), col * pitch, row * Pitch, 45, Height));
        }

        return words;
    }

    /// <summary>One row of words a space apart, from <paramref name="x"/>, on line <paramref name="line"/>.</summary>
    private static OcrWord[] Row(int line, int x, params string[] texts)
    {
        var row = new OcrWord[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            row[i] = new OcrWord(texts[i], x, line * Pitch, texts[i].Length * Glyph, Height);
            x += row[i].Width + Space;
        }

        return row;
    }

    private static OcrWord[] Page(params OcrWord[][] rows) => [.. rows.SelectMany(r => r)];

    /// <summary>The texts of the words a redaction painted over, sorted.</summary>
    private static List<string> MaskedTexts(FrameRedaction redaction, OcrWord[] words) =>
        [.. words.Where(w => Covered(redaction, w)).Select(w => w.Text).Order(StringComparer.Ordinal)];

    private static bool Covered(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);

    private static bool Touched(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.X < word.X + word.Width && word.X < r.X + r.Width
            && r.Y < word.Y + word.Height && word.Y < r.Y + r.Height);
}
