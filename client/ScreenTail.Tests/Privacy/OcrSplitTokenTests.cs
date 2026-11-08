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
    /// The word boxes' height for the loop's 24 pt label, and the gaps the engine left between them
    /// (ScreenTail.Tests.Windows OcrSplitSecretTests): a space was never narrower than 0.39 of the height
    /// at that size, and the pieces of a split token were never further apart than 0.33 of it. Both are
    /// taken at their edge here, so a rule that drifts either way fails.
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
    public void WordsASpaceApartAreStillLeftAlone()
    {
        // The control, as PatternLibraryGapTests.TheWordsAroundASecretAreLeftAlone is for one row of
        // words: a space on screen still ends the token, so the technician's own words stay readable.
        var words = Line(("pass:", Space), ("Tangerine4821", Split), ("Wobble", Space), ("for", Space), ("reception", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED] for reception", redaction.Text);
    }

    [Fact]
    public void TheNextLineIsNotPartOfTheToken()
    {
        // The pass line's last piece ends near the right edge and the next line starts at the left, below:
        // touching in the reading order, not on the screen.
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("Tangerine4821", 93, 100, 190, Height),
            new("Wobble", 291, 100, 96, Height),
            new("Queue", 24, 140, 80, Height),
            new("cleared", 113, 140, 100, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("pass: [REDACTED] Queue cleared", redaction.Text);
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
