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
        Assert.Equal("[REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[2]), "the second piece is left unpainted in the frame");
    }

    [Fact]
    public void ATokenReadAsThreeWordsIsMaskedWhole()
    {
        var words = Line(("pass:", Space), ("Tangerine", 0), ("4821", Split), ("Wobble", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("[REDACTED]", redaction.Text);
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

        Assert.Equal("[REDACTED]", redaction.Text);
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
        Assert.StartsWith("[REDACTED]", redaction.Text, StringComparison.Ordinal);
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

        Assert.Equal("[REDACTED]", redaction.Text);
        Assert.True(Covered(redaction, words[3]), "the end of the row is left unpainted in the frame");
        Assert.True(Covered(redaction, words[1]), "the stray word on the secret's side is left unpainted in the frame");
    }

    [Fact]
    public void APasswordAnotherRuleAlsoClaimsStillTakesItsRow()
    {
        // A card number used as a password: the card rule claims the same span first, and the merged
        // match carries the card's kind. It still came from a password cue, so the row goes with it,
        // under the card's marker.
        var words = Line(("pass:", Space), ("4111111111111111", Space), ("for", Space), ("reception", 0));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("[CARD]", redaction.Text);
    }

    [Fact]
    public void ACueInsideTheMatchedSpanFailsClosed()
    {
        // Security review of #268. A tenant pattern claims "ticket pass: Tang", so the merged match starts
        // at "ticket" and the cue is inside it. Measured from the cue to the span's start, the row seemed
        // to run right to left and its end, "reception", stayed readable.
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = ["ticket pass: Tang"] });
        OcrWord[] words =
        [
            new("ticket", 24, 100, 80, Height),
            new("pass:", 113, 100, 60, Height),
            new("Tangerine4821", 182, 100, 190, Height),
            new("reception", 381, 100, 110, Height),
        ];

        var redaction = engine.RedactFrame(words);

        Assert.DoesNotContain("reception", redaction.Text, StringComparison.Ordinal);
        Assert.True(CoveredByAny(redaction, words[3]), "the end of the row is left unpainted in the frame");
    }

    [Fact]
    public void APasswordMergedWithAnEarlierEmailIsAnchoredOnItsSecret()
    {
        // Security review of #268. An email match before the secret overlaps it, and the merged match
        // starts at the email: anchored there, the row's direction came out backwards.
        var engine = new RedactionEngine(RedactionPolicy.Default, (text, policy, incomplete) =>
        {
            var found = PatternLibrary.Find(text, policy, incomplete).ToList();
            found.Add(new PatternMatch(MaskKind.Email, 0, text.IndexOf("Tang", StringComparison.Ordinal) + 4, "[EMAIL]"));
            return found;
        });
        OcrWord[] words =
        [
            new("bob@example.com", 24, 100, 180, Height),
            new("pass:", 213, 100, 60, Height),
            new("Tangerine4821", 282, 100, 190, Height),
            new("reception", 481, 100, 110, Height),
        ];

        var redaction = engine.RedactFrame(words);

        Assert.DoesNotContain("reception", redaction.Text, StringComparison.Ordinal);
        Assert.True(CoveredByAny(redaction, words[3]), "the end of the row is left unpainted in the frame");
    }

    [Fact]
    public void AMergedSpanAcrossTwoRowsMasksBothRows()
    {
        // Security review of #268. A tenant pattern runs from "is" at the end of the cue's row to the
        // secret at the start of the next. Anchored on "is", the rule masked the rest of the first row and
        // left the secret's own row, "reception" on it, readable.
        var engine = new RedactionEngine(new RedactionPolicy { CustomPatterns = ["is Tang"] });
        OcrWord[] words =
        [
            new("pass:", 24, 100, 60, Height),
            new("is", 93, 100, 24, Height),
            new("Tangerine4821", 24, 140, 190, Height),
            new("reception", 223, 140, 110, Height),
        ];

        var redaction = engine.RedactFrame(words);

        Assert.DoesNotContain("reception", redaction.Text, StringComparison.Ordinal);
        Assert.True(CoveredByAny(redaction, words[3]), "the secret's row is left unpainted in the frame");
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

        // The cue goes too: in doubt, nothing on the rows involved is kept, the cue included.
        Assert.Equal("[REDACTED]", redaction.Text);
    }

    [Fact]
    public void ThePasswordsRowIsTheSameHoweverTheEngineListsIt()
    {
        // Security review of #268: list order is not reading order, so nothing in the row rule may be
        // kept or masked by where the engine listed a word. The same screen, listed two ways, masks the
        // same words. The cue stays just before its secret in both, as the text has to have it.
        OcrWord ticket = new("ticket", 24, 100, 80, Height);
        OcrWord cue = new("pass:", 113, 100, 60, Height);
        OcrWord secret = new("Tangerine4821", 182, 100, 190, Height);
        OcrWord tail = new("for", 381, 100, 40, Height);
        OcrWord end = new("reception", 430, 100, 110, Height);
        OcrWord next = new("Queue", 24, 140, 80, Height);
        OcrWord nextEnd = new("cleared", 113, 140, 100, Height);

        OcrWord[] listed = [ticket, cue, secret, tail, end, next, nextEnd];
        OcrWord[] reshuffled = [next, tail, cue, secret, nextEnd, end, ticket];
        var inOrder = Masked(Engine.RedactFrame(listed), listed);
        var shuffled = Masked(Engine.RedactFrame(reshuffled), reshuffled);

        Assert.Equal(inOrder, shuffled);
        Assert.Equal(["Tangerine4821", "for", "pass:", "reception"], inOrder);
    }

    [Fact]
    public void ARowInDoubtIsTheSameHoweverTheEngineListsIt()
    {
        // The fallback, cue on the row above: every word on the secret's row is masked, and the cue,
        // wherever each was listed. "note" listed before the cue was kept readable by its index.
        OcrWord cue = new("pass:", 600, 100, 60, Height);
        OcrWord above = new("ticket", 24, 100, 80, Height);
        OcrWord note = new("note", 24, 140, 60, Height);
        OcrWord secret = new("Tangerine4821", 200, 140, 190, Height);
        OcrWord end = new("reception", 400, 140, 110, Height);

        OcrWord[] listed = [above, cue, secret, end, note];
        OcrWord[] reshuffled = [note, above, cue, secret, end];
        var inOrder = Masked(Engine.RedactFrame(listed), listed);
        var shuffled = Masked(Engine.RedactFrame(reshuffled), reshuffled);

        Assert.Equal(inOrder, shuffled);
        Assert.Equal(["Tangerine4821", "note", "pass:", "reception"], inOrder);
    }

    [Fact]
    public void ALatinRowReadsLeftToRightWhereverItsWordsSit()
    {
        // The rule infers no direction (owner proxy 2026-11-09): ScreenTail's cues are Latin, so on the
        // cue's row only what lies wholly left of the cue stays. This row was read right to left before.
        OcrWord[] words =
        [
            new("ticket", 640, 100, 80, Height),
            new("pass:", 560, 100, 60, Height),
            new("Tangerine4821", 330, 100, 190, Height),
            new("reception", 200, 100, 110, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("[REDACTED] reception", redaction.Text);
        Assert.True(Covered(redaction, words[0]), "the word right of the cue is left unpainted in the frame");
    }

    [Fact]
    public void ARowWithRightToLeftScriptIsMaskedWhole()
    {
        // A Hebrew or Arabic word on a row means it may read right to left, and nothing on it is kept.
        OcrWord[] words =
        [
            new("\u05E9\u05DC\u05D5\u05DD", 24, 100, 70, Height),
            new("pass:", 110, 100, 60, Height),
            new("Tangerine4821", 179, 100, 190, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal("[REDACTED]", redaction.Text);
        Assert.True(CoveredByAny(redaction, words[0]), "the right-to-left word is left unpainted in the frame");
    }

    [Fact]
    public void TheCuesOwnRowIsMaskedWhenTheSecretIsOnTheNext()
    {
        // Review of #268, leak 1: the secret the rule took ("Note7") is on the next row, and the cue's
        // own row, "sunshine" on it, was never masked.
        OcrWord[] words =
        [
            new("Password:", 24, 140, 110, Height),
            new("Note7", 24, 180, 70, Height),
            new("sunshine", 300, 140, 100, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("sunshine", redaction.Text, StringComparison.Ordinal);
        Assert.True(CoveredByAny(redaction, words[2]), "the cue's row is left unpainted in the frame");
    }

    [Fact]
    public void TheWordAfterTheCueOnScreenIsMaskedWhateverTheListSays()
    {
        // Review of #268, leak 2: the word listed after the cue sits left of it, so the row's direction,
        // read from it, came out backwards and the word right of the cue stayed.
        OcrWord[] words =
        [
            new("Password:", 400, 100, 110, Height),
            new("Ref7", 24, 100, 50, Height),
            new("sunshine", 530, 100, 100, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("sunshine", redaction.Text, StringComparison.Ordinal);
        Assert.True(CoveredByAny(redaction, words[2]), "the word right of the cue is left unpainted in the frame");
    }

    [Fact]
    public void AWordLeftOfTheCueStaysAndTheWordRightOfItGoes()
    {
        // Review of #268, leak 2's variant: "Ticket", left of the cue, was masked and "sunshine", right of
        // it, was kept.
        OcrWord[] words =
        [
            new("Password:", 400, 100, 110, Height),
            new("A12", 104, 100, 40, Height),
            new("sunshine", 530, 100, 100, Height),
            new("Ticket", 24, 100, 70, Height),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.DoesNotContain("sunshine", redaction.Text, StringComparison.Ordinal);
        Assert.EndsWith(" Ticket", redaction.Text, StringComparison.Ordinal);
        Assert.False(CoveredByAny(redaction, words[3]), "the word left of the cue is painted over");
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

        Assert.Equal("[REDACTED] Queue cleared", redaction.Text);
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

        Assert.Equal("[REDACTED]", redaction.Text);
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

        Assert.Equal("[REDACTED]", redaction.Text);
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

    /// <summary>
    /// The words a redaction took both out of the stored text and under a painted box, sorted. Each test
    /// word's text is unique, so a word is gone from the text when its text is.
    /// </summary>
    private static List<string> Masked(FrameRedaction redaction, OcrWord[] words)
    {
        var kept = redaction.Text.Split(' ');
        return [.. words
            .Where(w => !kept.Contains(w.Text, StringComparer.Ordinal) && CoveredByAny(redaction, w))
            .Select(w => w.Text)
            .Order(StringComparer.Ordinal)];
    }

    private static bool CoveredByAny(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);

    private static bool Covered(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.Kind == MaskKind.Password
            && r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);
}
