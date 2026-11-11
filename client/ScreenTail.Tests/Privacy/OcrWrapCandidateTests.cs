using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The wrap pass against the pages a security review of #275 left a secret showing on (2026-11-10):
/// a row whose real spaces are narrow enough to read as none, and a continuation that is not the one
/// run the pass first looks at. See <see cref="OcrWrapTests"/> for the rule itself.
/// </summary>
public sealed class OcrWrapCandidateTests
{
    private const int Height = 20;
    private const int Glyph = 10;
    private const int Pitch = 30;
    private const int Left = 40;

    /// <summary>A space at half the row's height, and one at 0.3 of it, which the token rule joins.</summary>
    private const int Wide = 10;
    private const int Narrow = 6;

    private const string KeyStart = "sk_live_ABCDEFGHIJ";
    private const string KeyRest = "KLMNOPQRSTUV0123";

    private static readonly RedactionEngine Engine = new();

    [Theory]
    [InlineData(Wide)]
    [InlineData(Narrow)]
    public void AWrappedPasswordIsMaskedWhateverTheSpacesMeasure(int space)
    {
        // With narrow spaces the row above read as one word, "wifipasswordTangerine4821Wob", and no
        // cue was found in it.
        var words = Page(Row(0, Left, space, "wifi", "password", "Tangerine4821Wob"), Row(1, Left, space, "ble", "x"));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("ble", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tangerine", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[3]), "the password's second row is left unpainted in the frame");
    }

    [Theory]
    [InlineData(Wide)]
    [InlineData(Narrow)]
    public void AWrappedCardNumberIsMaskedWhateverTheSpacesMeasure(int space)
    {
        var words = Page(Row(0, Left, space, "order", "4111111111"), Row(1, Left, space, "111111", "units"));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("4111111111", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("111111", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[1]) && Covered(redaction, words[2]), "a piece of the card number is left unpainted in the frame");
    }

    [Theory]
    [InlineData(Wide)]
    [InlineData(Narrow)]
    public void AWrappedSocialSecurityNumberIsMaskedWhateverTheSpacesMeasure(int space)
    {
        var words = Page(Row(0, Left, space, "number", "123-45-"), Row(1, Left, space, "6789", "on"));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("123-45-", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("6789", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[1]) && Covered(redaction, words[2]), "a piece of the number is left unpainted in the frame");
    }

    [Fact]
    public void AWrapInTheLeftPaneWithAPaneCloseBesideItIsMasked()
    {
        // The right-hand pane starts a row height and a half after the left one ends: more than any
        // space, less than the two row heights that used to cut a row into runs.
        var above = Row(0, Left, Wide, "deploy", "--key", KeyStart);
        var below = Row(1, Left, Wide, KeyRest, "--region", "eu");
        var words = Page(
            [.. above, .. Row(0, Right(above) + 30, Wide, "Notes", "pane")],
            [.. below, .. Row(1, Right(above) + 30, Wide, "second", "note")]);

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
        Assert.Contains("Notes pane", redaction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrapBesideADividerIsMasked()
    {
        // Two panes of a terminal with a line drawn between them, a space either side.
        var words = Page(
            Row(0, Left, Wide, "deploy", "--key", KeyStart, "|", "Notes", "pane"),
            Row(1, Left, Wide, KeyRest, "--region", "eu", "|", "second", "note"));

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
        Assert.Contains("Notes pane", redaction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AContinuationFurtherFromTheStartThanAnotherRunIsMasked()
    {
        // The run above starts in from the left; the row below has the continuation at the left edge
        // and an unrelated run whose start is nearer.
        var words = Page(
            Row(0, 200, Wide, "--key", KeyStart),
            [.. Row(1, Left, Wide, KeyRest), .. Row(1, 260, Wide, "zz")]);

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
    }

    [Fact]
    public void AContinuationOnTheSecondRowDownIsMasked()
    {
        // An unrelated short row comes first, a status line say, set close under the row above.
        var words = Page(
            Row(0, Left, Wide, "deploy", "--key", KeyStart),
            [new OcrWord("zz", Left, 22, 20, 6)],
            Row(1, Left, Wide, KeyRest, "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
    }

    [Fact]
    public void AContinuationAfterAGutterIsMasked()
    {
        // A line number and a rule before the text, as an editor draws them.
        var words = Page(
            Row(0, Left, Wide, "12", "|", "deploy", "--key", KeyStart),
            Row(1, Left, Wide, "13", "|", KeyRest, "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
    }

    [Theory]
    [InlineData(KeyStart + "-", KeyRest)]
    [InlineData(KeyStart + "\\", KeyRest)]
    [InlineData(KeyStart, ">" + KeyRest)]
    [InlineData(KeyStart, "+" + KeyRest)]
    [InlineData(KeyStart + "\\", ">" + KeyRest)]
    public void AContinuationMarkerReadIntoTheWordIsDropped(string start, string rest)
    {
        var words = Page(Row(0, Left, Wide, "deploy", "--key", start), Row(1, Left, Wide, rest, "--region", "eu"));

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
    }

    [Theory]
    [InlineData("\\", "")]
    [InlineData("-", "")]
    [InlineData("", ">")]
    [InlineData("", "+")]
    [InlineData("\\", ">")]
    public void AContinuationMarkerReadAsAWordIsStepped(string after, string before)
    {
        string[] above = after.Length > 0 ? ["deploy", "--key", KeyStart, after] : ["deploy", "--key", KeyStart];
        string[] below = before.Length > 0 ? [before, KeyRest, "--region", "eu"] : [KeyRest, "--region", "eu"];
        var words = Page(Row(0, Left, Wide, above), Row(1, Left, Wide, below));

        var redaction = Engine.RedactFrame(words);

        AssertKeyMasked(redaction, words);
    }

    [Fact]
    public void AChainThroughARowWithARightToLeftWordIsMasked()
    {
        // Three rows of one signed token. The middle row has a Hebrew word before its piece, so it is
        // joined from above right to left, and on to the row below left to right.
        var words = Page(
            Row(0, Left, Wide, "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIx"),
            Row(1, Left, Wide, "מפתח", "MjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2Mj"),
            Row(2, Left, Wide, "M5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c", "ok"));

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("eyJ", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("MjM0", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SflK", redaction.Text, StringComparison.Ordinal);
        Assert.True(Covered(redaction, words[0]) && Covered(redaction, words[2]) && Covered(redaction, words[3]), "a piece of the token is left unpainted in the frame");
    }

    private static void AssertKeyMasked(FrameRedaction redaction, OcrWord[] words)
    {
        Assert.DoesNotContain("sk_live", redaction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("KLMNOP", redaction.Text, StringComparison.Ordinal);
        Assert.All(
            words.Where(w => w.Text.Contains("sk_live", StringComparison.Ordinal) || w.Text.Contains("KLMNOP", StringComparison.Ordinal)),
            w => Assert.True(Covered(redaction, w), "a piece of the key is left unpainted in the frame"));
    }

    private static int Right(OcrWord[] row) => row[^1].X + row[^1].Width;

    private static OcrWord[] Row(int line, int x, int space, params string[] texts)
    {
        var row = new OcrWord[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            row[i] = new OcrWord(texts[i], x, line * Pitch, texts[i].Length * Glyph, Height);
            x += row[i].Width + space;
        }

        return row;
    }

    private static OcrWord[] Page(params OcrWord[][] rows) => [.. rows.SelectMany(r => r)];

    private static bool Covered(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);
}
