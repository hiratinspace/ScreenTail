using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The token, row and wrap rules on the same page at 100%, 125% and 150% (ST-042, after #268 and #269).
///
/// Every rule measures a gap against a height, so a page scaled exactly is the same page to them. A
/// real one is not scaled exactly: boxes are whole pixels, so at 125% a 9 px space is 11.25 px and
/// comes back as 11 or 12, and a 24 px row as 30. These scale each page's boxes and round them every
/// way a box can be rounded (each edge to the nearest pixel, the box grown to whole pixels, the box
/// shrunk to whole pixels, and position and size rounded apart, which is what
/// WindowsOcrRecogniser does), and ask for the same words masked as at 100%.
///
/// What this cannot show is what the OCR engine itself does at those sizes; that is
/// OcrSplitSecretTests and OcrScaledSecretTests in the Windows-only suite.
/// </summary>
public sealed class OcrScalingTests
{
    private const int Height = 24;
    private const int Glyph = 12;
    private const int Pitch = 36;
    private const int Left = 24;

    /// <summary>A space on the screen: 0.5 of the row's height, about the middle of what was measured.</summary>
    private const int Space = 12;

    /// <summary>
    /// A split inside a token: 0.25 of the row's height. The widest measured at 100% was 0.33, a
    /// pixel or less under the cutoff, and a pixel of rounding is more than that; see
    /// <see cref="ASplitOfAQuarterOfTheRowIsJoinedAtEveryScaleWhenRoundedToTheNearestPixel"/>.
    /// </summary>
    private const int Split = 6;

    private static readonly RedactionEngine Engine = new(new RedactionPolicy { Emails = true });

    public static TheoryData<double, string> ScalesAndRoundings()
    {
        var data = new TheoryData<double, string>();
        foreach (var scale in new[] { 1.25, 1.5 })
        {
            foreach (var rounding in new[] { "edges", "grown", "shrunk", "position and size" })
            {
                data.Add(scale, rounding);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScalesAndRoundings))]
    public void TheSameWordsAreMaskedAtEveryScale(double scale, string rounding)
    {
        foreach (var (name, words) in Pages())
        {
            var expected = Masked(Engine.RedactFrame(words), words);
            Assert.True(expected.Count > 0, $"{name}: nothing is masked at 100%, so the page proves nothing");

            var scaled = Scaled(words, scale, rounding);
            var actual = Masked(Engine.RedactFrame(scaled), scaled);

            Assert.True(expected.SequenceEqual(actual), $"{name} at {scale}, {rounding}: masked words {string.Join(",", actual)}, at 100% {string.Join(",", expected)}");
        }
    }

    [Theory]
    [MemberData(nameof(ScalesAndRoundings))]
    public void RowsAndJoinsAreTheSameAtEveryScale(double scale, string rounding)
    {
        // Random screens of lines: words of every length, tokens split in two, spaces, a second pane.
        var random = new Random(271);
        for (var page = 0; page < 200; page++)
        {
            var words = new List<OcrWord>();
            var lines = random.Next(2, 9);
            for (var line = 0; line < lines; line++)
            {
                var x = Left + (random.Next(0, 3) * 4 * Glyph);
                var count = random.Next(1, 9);
                for (var i = 0; i < count; i++)
                {
                    // Lowercase words are x-height boxes set lower in the line.
                    var low = random.Next(0, 3) == 0;
                    var width = random.Next(1, 12) * Glyph;
                    words.Add(new OcrWord("w", x, (line * Pitch) + (low ? 7 : 0), width, low ? 17 : Height));
                    x += width + random.Next(0, 6) switch { 0 => Split, 1 => 10 * Glyph, _ => Space };
                }
            }

            var scaled = Scaled(words, scale, rounding);
            var (before, after) = (new OcrPage(words), new OcrPage(scaled));
            for (var a = 0; a < words.Count; a++)
            {
                for (var b = a + 1; b < words.Count; b++)
                {
                    var sameRow = before.RowOf(a) == before.RowOf(b);
                    Assert.True(sameRow == (after.RowOf(a) == after.RowOf(b)), $"page {page} at {scale}, {rounding}: words {a} and {b} change rows");
                    if (sameRow)
                    {
                        // Clear of the cutoff by more than a pixel of rounding can move it. Between
                        // the two a pair may fall either side, which is the limit stated above.
                        var share = RedactionEngine.GapShare(words[a], words[b], before.RowHeight(a));
                        var joinedAfter = RedactionEngine.GapShare(scaled[a], scaled[b], after.RowHeight(a)) <= RedactionEngine.TokenGap;
                        Assert.False(share <= 0.26 && !joinedAfter, $"page {page} at {scale}, {rounding}: words {a} and {b} are one token at 100% and apart when scaled");
                        Assert.False(share >= 0.45 && joinedAfter, $"page {page} at {scale}, {rounding}: words {a} and {b} are a space apart at 100% and one token when scaled");
                    }
                }
            }
        }
    }

    /// <summary>
    /// How wide a split still joins once the page is scaled and its boxes rounded to the nearest
    /// pixel, on rows from 9 pt to 24 pt text: a quarter of the row's height does, at every scale.
    /// The widest split measured at 100% was 0.33 (OcrSplitSecretTests, 9 pt), a pixel or less under
    /// the cutoff of 0.35, and that does not survive rounding in this model: 4 px on a 13 px row is
    /// 6 px on 16 at 125%, 0.375. Whether the engine reads such a split at 125% is for the Windows-only
    /// probes to say; this pins what arithmetic alone guarantees.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void ASplitOfAQuarterOfTheRowIsJoinedAtEveryScaleWhenRoundedToTheNearestPixel(double scale)
    {
        for (var height = 12; height <= 32; height++)
        {
            var gap = (int)Math.Floor(0.25 * height);
            for (var x = 0; x < 8; x++)
            {
                OcrWord[] words =
                [
                    new("token:", x, 0, 70, height),
                    new("ABCDEFGHIJKLMNOPQRSTUVWXYZ12", x + 70 + height, 0, 300, height),
                    new("34xy", x + 70 + height + 300 + gap, 0, 40, height),
                ];
                var scaled = Scaled(words, scale, "edges");

                var redaction = Engine.RedactFrame(scaled);

                Assert.True(
                    redaction.Text == "token: [SECRET]",
                    $"a {gap} px split on a {height} px row at {scale}: the tail is left showing (gap {scaled[2].X - scaled[1].X - scaled[1].Width} px on {scaled[1].Height} px)");
            }
        }
    }

    private static IEnumerable<(string Name, OcrWord[] Words)> Pages()
    {
        var key = "ghp_" + new string('A', 36);
        yield return ("a password read as two words", Row(0, Left, ("wifi", Space), ("pass:", Space), ("Tangerine4821", Split), ("Wobble", Space), ("for", Space), ("reception", 0)));
        yield return ("a key read as three words", Row(0, Left, ("deploy", Space), (key, Split), ("Wobble7", Split), ("x9", Space), ("done", 0)));
        yield return ("an email between words", Row(0, Left, ("mail", Space), ("bob@example.com", Space), ("for", Space), ("reception", 0)));
        yield return ("a lowercase piece set low", [
            .. Row(0, Left, ("deploy", Space), (key, Split)),
            new OcrWord("ocean", Left + (6 * Glyph) + Space + (key.Length * Glyph) + Split, 7, 5 * Glyph, 17),
            new OcrWord("done", Left + (6 * Glyph) + Space + (key.Length * Glyph) + Split + (5 * Glyph) + Space, 0, 4 * Glyph, Height),
        ]);
        yield return ("a password over a longer row", [
            .. Row(0, Left, ("wifi", Space), ("password:", Space), ("Tangerine4821", 0)),
            .. Row(1, Left, ("Queue", Space), ("cleared", Space), ("and", Space), ("a", Space), ("test", Space), ("page", Space), ("sent", 0)),
        ]);
        yield return ("a key wrapped onto the next row", [
            .. Row(0, Left, ("deploy", Space), ("--key", Space), ("sk_live_ABCDEFGHIJ", 0)),
            .. Row(1, Left, ("KLMNOPQRSTUV0123", Space), ("--region", Space), ("eu", 0)),
        ]);
        yield return ("a key wrapped over three rows, the middle one split", [
            .. Row(0, Left, ("auth", Space), ("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIx", 0)),
            .. Row(1, Left, ("MjM0NTY3ODkwIiwibmFtZSI6IkpvaG4g", Split), ("RG9lIiwiaWF0IjoxNTE2Mj", 0)),
            .. Row(2, Left, ("M5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c", Space), ("ok", 0)),
        ]);
        yield return ("a wrapped password in the right-hand pane", [
            .. Row(0, Left, ("Notes", Space), ("pane", 20 * Glyph), ("wifi", Space), ("password:", Space), ("Tangerine4821Wob", 0)),
            .. Row(1, Left, ("second", Space), ("note", 19 * Glyph), ("ble", Space), ("is", Space), ("on", 0)),
            .. Row(2, Left, ("third", Space), ("note", 20 * Glyph), ("Queue", Space), ("cleared", Space), ("and", Space), ("a", Space), ("test", Space), ("page", Space), ("sent", 0)),
        ]);
    }

    private static OcrWord[] Row(int line, int x, params (string Text, int GapAfter)[] words)
    {
        var row = new OcrWord[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            row[i] = new OcrWord(words[i].Text, x, line * Pitch, words[i].Text.Length * Glyph, Height);
            x += row[i].Width + words[i].GapAfter;
        }

        return row;
    }

    private static OcrWord[] Scaled(IReadOnlyList<OcrWord> words, double scale, string rounding) =>
        [.. words.Select(w =>
        {
            double left = w.X * scale, top = w.Y * scale, right = (w.X + w.Width) * scale, bottom = (w.Y + w.Height) * scale;
            var (l, t, r, b) = rounding switch
            {
                "edges" => (Round(left), Round(top), Round(right), Round(bottom)),
                "grown" => ((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom)),
                "shrunk" => ((int)Math.Ceiling(left), (int)Math.Ceiling(top), (int)Math.Floor(right), (int)Math.Floor(bottom)),
                _ => (Round(left), Round(top), Round(left) + Round(right - left), Round(top) + Round(bottom - top)),
            };
            return new OcrWord(w.Text, l, t, r - l, b - t);
        })];

    private static int Round(double value) => (int)Math.Round(value);

    /// <summary>The places of the words a redaction painted over.</summary>
    private static List<int> Masked(FrameRedaction redaction, OcrWord[] words) =>
        [.. Enumerable.Range(0, words.Length).Where(i => redaction.Regions.Any(r =>
            r.X <= words[i].X && r.Y <= words[i].Y
            && r.X + r.Width >= words[i].X + words[i].Width
            && r.Y + r.Height >= words[i].Y + words[i].Height))];
}
