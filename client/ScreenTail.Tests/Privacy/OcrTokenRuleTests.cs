using System.Diagnostics;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The token rule on its own, with no password cue anywhere (review of #268, MAJOR 3). A password takes
/// the rest of its row, which would hide whatever the token rule gets wrong, so these are API keys, a
/// token and emails the OCR engine split into pieces. Also the boxes a redaction paints, and what a big
/// frame costs.
/// </summary>
public sealed class OcrTokenRuleTests
{
    private const int Height = 24;

    private static readonly RedactionEngine Engine = new();
    private static readonly RedactionEngine WithEmails = new(new RedactionPolicy { Emails = true });

    /// <summary>A GitHub token's shape, which the API key rule matches on its own.</summary>
    private static readonly string Key = "ghp_" + new string('A', 36);

    [Fact]
    public void AKeysPieceListedInAnotherLineIsMaskedToo()
    {
        // The tail touches the key on screen, and the engine listed it before the key.
        OcrWord[] words =
        [
            new("deploy", 24, 100, 70, Height),
            new("Wobble7", 507, 100, 80, Height),
            new("done", 600, 100, 50, Height),
            new(Key, 104, 100, 400, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("deploy [SECRET] done [SECRET]", redaction.Text);
        Assert.True(Covered(redaction, words[1]), "the key's tail is left unpainted in the frame");
    }

    [Fact]
    public void ATokensPieceOnTheNextLineOfTheListIsMaskedToo()
    {
        // A value introduced as a token, its last characters read as a word of their own and listed
        // after the next row.
        OcrWord[] words =
        [
            new("token:", 24, 100, 70, Height),
            new("ABCDEFGHIJKLMNOPQRSTUVWXYZ12", 104, 100, 300, Height),
            new("Queue", 24, 140, 80, Height),
            new("cleared", 113, 140, 100, Height),
            new("34xy", 406, 100, 40, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("token: [SECRET] Queue cleared [SECRET]", redaction.Text);
        Assert.True(Covered(redaction, words[4]), "the token's tail is left unpainted in the frame");
    }

    [Fact]
    public void APieceSetLowerThanASmallHighBoxIsStillOnTheKeysRow()
    {
        // Security review of #268: rows were grouped by their first word only. The dash is a small box
        // set high, the key shares its row, and the key's tail, set a little lower, shares a row with the
        // key but not with the dash, so it became a row of its own the token rule never looked at.
        OcrWord[] words =
        [
            new("-", 24, 100, 10, 6),
            new(Key, 104, 98, 400, Height),
            new("Wobble7", 507, 106, 80, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("Wobble7", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[2]), "the key's tail is left unpainted in the frame");
    }

    [Fact]
    public void ARowIsEveryWordReachedThroughWordsThatShareOne()
    {
        // Against the plain definition, pairwise and joined through, on pages of boxes of every height
        // scattered over a few lines' worth of space.
        var random = new Random(268);
        for (var page = 0; page < 300; page++)
        {
            var words = Enumerable.Range(0, 40)
                .Select(i => new OcrWord("w", random.Next(0, 800), random.Next(0, 160), 20, random.Next(0, 4) == 0 ? random.Next(0, 60) : random.Next(4, 30)))
                .ToList();
            var rows = new OcrPage(words);

            var group = Enumerable.Range(0, words.Count).ToArray();
            int Find(int i) => group[i] == i ? i : group[i] = Find(group[i]);
            for (var a = 0; a < words.Count; a++)
            {
                for (var b = a + 1; b < words.Count; b++)
                {
                    if (RedactionEngine.SameRow(words[a], words[b]))
                    {
                        group[Find(a)] = Find(b);
                    }
                }
            }

            for (var a = 0; a < words.Count; a++)
            {
                for (var b = a + 1; b < words.Count; b++)
                {
                    Assert.True(
                        (Find(a) == Find(b)) == (rows.RowOf(a) == rows.RowOf(b)),
                        $"page {page}: words {a} and {b} are {(Find(a) == Find(b) ? "joined" : "apart")} by SameRow but not by the row index");
                }
            }
        }
    }

    [Fact]
    public void AnEmailsPieceToTheLeftIsMaskedToo()
    {
        // "jane.doe@example.com" read as "jane." and "doe@example.com"; the rule matches the second, the
        // first sits to its left and is listed last.
        OcrWord[] words =
        [
            new("mail", 24, 100, 50, Height),
            new("doe@example.com", 140, 100, 180, Height),
            new("Inbox", 400, 100, 60, Height),
            new("jane.", 92, 100, 46, Height),
        ];

        var redaction = WithEmails.RedactFrame(words);

        Assert.Equal("mail [EMAIL] Inbox [EMAIL]", redaction.Text);
        Assert.True(Covered(redaction, words[3]), "the email's first piece is left unpainted in the frame");
    }

    [Fact]
    public void AnEmailsShortLowercasePiecesAreMeasuredAgainstTheirRow()
    {
        // Both pieces are x-height boxes, 17 px on a row of 24 px words. Against their own height the
        // 7 px gap is 0.41 and reads as a space; against the row's it is 0.29, a split.
        OcrWord[] words =
        [
            new("Inbox", 24, 100, 60, Height),
            new("jane@example.co", 100, 105, 200, 17),
            new("m", 307, 105, 12, 17),
            new("Reply", 500, 100, 60, Height),
            new("Sent", 600, 100, 50, Height),
        ];

        var redaction = WithEmails.RedactFrame(words);

        Assert.Equal("Inbox [EMAIL] Reply Sent", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the short piece is left unpainted in the frame");
    }

    [Fact]
    public void TwoMatchesWhoseRunsComeOutOfOrderAreBothMasked()
    {
        // The email's last piece is listed at the end, after the key on the next row, so the email's
        // runs in the text come before and after the key's. Unsorted, the key's run was swallowed into
        // the email's and the key stayed in the text.
        OcrWord[] words =
        [
            new("mail", 24, 100, 50, Height),
            new("jane@example.co", 100, 100, 200, Height),
            new("deploy", 24, 140, 70, Height),
            new(Key, 104, 140, 400, Height),
            new("done", 520, 140, 50, Height),
            new("m", 302, 100, 12, Height),
        ];

        var redaction = WithEmails.RedactFrame(words);

        Assert.Equal("mail [EMAIL] deploy [SECRET] done [EMAIL]", redaction.Text);
    }

    [Fact]
    public void ARedactionPaintsOnlyTheWordsItTookFromTheText()
    {
        // One box per masked run on a row, not one around the whole match: the fallback takes the cue's
        // row and the secret's, and a single box around both covered "ticket", which the text keeps.
        OcrWord[] words =
        [
            new("ticket", 24, 100, 80, Height),
            new("pass:", 600, 100, 60, Height),
            new("Tangerine4821", 200, 140, 190, Height),
            new("reception", 400, 140, 110, Height),
            new("note", 24, 140, 60, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.StartsWith("ticket ", redaction.Text, StringComparison.Ordinal);
        Assert.False(Touched(redaction, words[0]), "a word the text keeps is painted over");
        Assert.All(words.Skip(1), w => Assert.True(Covered(redaction, w), "a masked word is left unpainted in the frame"));
    }

    [Fact]
    public void AZeroHeightBoxIsStillPaintedAtItsRowsHeight()
    {
        OcrWord[] words =
        [
            new("Inbox", 24, 100, 60, Height),
            new("jane@example.com", 100, 112, 200, 0),
            new("Reply", 400, 100, 60, Height),
        ];

        var redaction = WithEmails.RedactFrame(words);

        var region = Assert.Single(redaction.Regions);
        Assert.True(region.Height >= Height, "the zero-height word was painted as a line");
        Assert.True(region.Y <= 100 && region.Y + region.Height >= 124, "the paint misses the row");
    }

    /// <summary>
    /// What a big frame costs (review of #268, MAJOR 4). The budget is 700 ms a frame on the laptop; the
    /// row rule and the token search were quadratic and took 300 ms to 2 s on the Mac for these. Best of
    /// three, so a busy runner's one slow pass does not fail it.
    /// </summary>
    [Theory]
    [InlineData("8,000 words, a password on every row")]
    [InlineData("2,500 password cues in 5,000 words")]
    [InlineData("a private key with no end, 5,000 words")]
    public void ABigFrameRedactsWithinBudget(string frame)
    {
        var words = frame switch
        {
            "8,000 words, a password on every row" => Grid(8000, (row, col) => col == 0 ? "password:" : col == 1 ? $"Xy{row}ab" : $"word{col}"),
            "2,500 password cues in 5,000 words" => Grid(5000, (row, col) => col % 2 == 0 ? "pwd:" : $"Ab{(row * 40) + col}"),
            _ => [new("-----BEGIN", 0, 0, 100, Height), new("PRIVATE", 110, 0, 80, Height), new("KEY-----", 200, 0, 80, Height), .. Grid(5000, (row, col) => $"w{row}x{col}", top: 30)],
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

    /// <summary>Rows of 40 words, 30 px apart.</summary>
    private static List<OcrWord> Grid(int count, Func<int, int, string> text, int top = 0)
    {
        var words = new List<OcrWord>(count);
        for (var i = 0; i < count; i++)
        {
            var (row, col) = (i / 40, i % 40);
            words.Add(new(text(row, col), col * 50, top + (row * 30), 45, Height));
        }

        return words;
    }

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
