using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// A token on screen that the OCR engine read as more than one word (loop-13 frame-text, 2026-11-08).
///
/// The end-to-end loop shows "pass: Tangerine4821Wobble", one token with no space in it. Windows OCR
/// sometimes returns it as "Tangerine4821" and "Wobble". The password rule ends a value at the first
/// plain word, so "Wobble" stayed in the stored OCR text and outside the painted mask: four hosted runs
/// in 72 failed on exactly that, a six-letter part of the secret stored. What separates the two pieces
/// from two words is on the screen, not in the text: the gap between their boxes is narrower than a
/// space. These are shaped like the engine's own result for the loop's 24 pt label.
/// </summary>
public sealed class OcrSplitTokenTests
{
    private static readonly RedactionEngine Engine = new();

    /// <summary>
    /// The word boxes' height for the loop's 24 pt label, and gaps either side of the token rule's
    /// line (0.35 of the row's height): the widest split the engine left at 9 pt was 0.33, and 9 px is
    /// 0.375. Both sit next to the line, so a rule that drifts either way fails.
    /// </summary>
    private const int Height = 24;
    private const int Space = 9;
    private const int Split = 8;

    [Fact]
    public void ATokenReadAsTwoWordsIsMaskedWhole()
    {
        // The shape the loop failed on: split where the digits meet the second word.
        var words = Line(("pass:", Space), ("Tangerine4821", Split), ("Wobble", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("Wobble", redaction.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tangerine", redaction.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("pass: [REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the second piece is left unpainted in the frame");
    }

    [Fact]
    public void ATokenReadAsThreeWordsIsMaskedWhole()
    {
        var words = Line(("pass:", Space), ("Tangerine", 0), ("4821", Split), ("Wobble", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[3]), "the last piece is left unpainted in the frame");
    }

    [Fact]
    public void APieceBeforeTheMatchIsMaskedToo()
    {
        // No colon, so nothing asserts a value and the plain first piece is read as prose: the rule
        // masked "4821Wobble" and kept "Tangerine", nine letters of the secret.
        var words = Line(("password", Space), ("Tangerine", Split), ("4821Wobble", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("Tangerine", redaction.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Wobble", redaction.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(Covered(redaction, words[1]), "the first piece is left unpainted in the frame");
    }

    [Fact]
    public void AnEmailKeepsTheWordsASpaceAfterIt()
    {
        // The token rule's control: a space on screen still ends the token. The row rule is the password
        // rule's alone (owner decision 2026-11-09), so the words after any other match stay readable.
        var engine = new RedactionEngine(new RedactionPolicy { Emails = true });
        var words = Line(("mail", Space), ("bob@example.com", Space), ("for", Space), ("reception", 0));

        var redaction = engine.RedactFrame(words);

        Assert.Equal("mail [EMAIL] for reception", redaction.Text);
    }

    [Fact]
    public void APasswordSplitWiderThanATokenIsMaskedByItsRow()
    {
        // 0.6 of the height between the pieces: wider than any split the probe measured, so the token
        // rule leaves it, and a remote viewer's scaling can make one. The row rule takes it anyway.
        var words = Line(("pass:", Space), ("Tangerine4821", 14), ("Wobble", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the far piece is left unpainted in the frame");
    }

    [Fact]
    public void ThePasswordsRowIsMaskedAndTheNextRowIsNot()
    {
        // The row is found on the screen: the next row's words are listed before the end of the pass
        // row, and the pass row's last word is listed last.
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("Tangerine4821", 93, 100, 190, Height),
            new("Queue", 24, 140, 80, Height),
            new("cleared", 113, 140, 100, Height),
            new("reception", 340, 100, 110, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("reception", redaction.Text, StringComparison.Ordinal);
        Assert.Contains("Queue cleared", redaction.Text, StringComparison.Ordinal);
        Assert.StartsWith("pass: [REDACTED]", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[4]), "the end of the pass row is left unpainted in the frame");
    }

    [Fact]
    public void AStrayWordOutOfOrderDoesNotTurnTheRowAround()
    {
        // Security review of #268. The engine listed "is" between the cue and the secret, but on the
        // screen it sits right of the secret. Read from that one neighbour, the row seemed to run right
        // to left, so the rule masked the cue's side and left the end of the row readable. The way the
        // row runs is the cue's to the secret's.
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("is", 300, 100, 24, Height),
            new("Tangerine4821", 93, 100, 190, Height),
            new("reception", 340, 100, 110, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("reception", redaction.Text, StringComparison.Ordinal);
        Assert.StartsWith("pass:", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[3]), "the end of the row is left unpainted in the frame");
        Assert.True(Covered(redaction, words[1]), "the stray word on the secret's side is left unpainted in the frame");
    }

    [Fact]
    public void ARowWhoseCueIsOnAnotherRowIsMaskedWhole()
    {
        // Fails closed: the cue ends the row above, so nothing on the secret's row says which way it
        // runs, and all of it is masked.
        OcrWord[] words =
        [
            new("pass:", 600, 100, 60, Height),
            new("Tangerine4821", 200, 140, 190, Height),
            new("reception", 400, 140, 110, Height),
            new("ticket", 24, 140, 80, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED]", redaction.Text);
    }

    [Fact]
    public void ThePasswordsRowRunsTheWayTheLineReads()
    {
        // Right to left: the row's end is to the left of the secret, and the words to the right of the
        // cue, before it in the reading order, stay readable.
        OcrWord[] words =
        [
            new("ticket", 640, 100, 80, Height),
            new("pass:", 560, 100, 60, Height),
            new("Tangerine4821", 330, 100, 190, Height),
            new("reception", 200, 100, 110, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("ticket pass: [REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[3]), "the end of the row is left unpainted in the frame");
    }

    [Fact]
    public void TheNextLineIsNotPartOfTheToken()
    {
        // The next line's first word sits just right of the pass line's last piece and just below it,
        // closer than a space sideways: only the rows tell them apart. Without the vertical check
        // "Queue" would be taken into the token.
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("Tangerine4821", 93, 100, 190, Height),
            new("Wobble", 291, 100, 96, Height),
            new("Queue", 389, 126, 80, Height),
            new("cleared", 478, 126, 100, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED] Queue cleared", redaction.Text);
    }

    [Theory]
    [InlineData("ocean")]
    [InlineData("snow")]
    public void AShortLowercasePieceIsMaskedToo(string piece)
    {
        // A piece with no capital, ascender or digit has a box only as tall as the x-height, about 0.7 of
        // its neighbours'. Measured against its own height the gap looked a third wider than it is, and a
        // split of 0.29 of the row's height read as 0.41 and was left showing (review of #268).
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("Tangerine4821", 93, 100, 190, Height),
            new(piece, 290, 105, piece.Length * 12, 17),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the short piece is left unpainted in the frame");
    }

    [Fact]
    public void APieceTheEngineListedOnAnotherLineIsMaskedToo()
    {
        // Windows can put a piece in an OcrLine of its own, so in the reading order it comes after the
        // next line's words. On the screen it still touches the rest of the token.
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("Tangerine4821", 93, 100, 190, Height),
            new("Queue", 24, 140, 80, Height),
            new("cleared", 113, 140, 100, Height),
            new("Wobble", 291, 100, 96, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("Wobble", redaction.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Queue cleared", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[4]), "the piece on its own line is left unpainted in the frame");
    }

    [Fact]
    public void APieceToTheLeftIsMaskedToo()
    {
        // Right to left: the reading order runs against the boxes, so the next piece is to the left.
        OcrWord[] words =
        [
            new("pass:", 500, 100, 60, Height),
            new("Tangerine4821", 301, 100, 190, Height),
            new("Wobble", 197, 100, 96, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the piece to the left is left unpainted in the frame");
    }

    /// <summary>One row of words, each followed by the given gap in pixels before the next.</summary>
    private static OcrWord[] Line(params (string Text, int GapAfter)[] words)
    {
        var line = new OcrWord[words.Length];
        var x = 24;
        for (var i = 0; i < words.Length; i++)
        {
            var width = words[i].Text.Length * 12;
            line[i] = new OcrWord(words[i].Text, x, 100, width, Height);
            x += width + words[i].GapAfter;
        }

        return line;
    }

    private static bool Covered(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.Kind == MaskKind.Password
            && r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);
}
