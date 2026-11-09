using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// The end-to-end loop's secret against the real OCR engine (loop-13 frame-text, 2026-11-08).
///
/// The loop puts "pass: Tangerine4821Wobble" on screen: two words and four digits with no space between
/// them, one token to anyone reading it. The engine does not always read it as one. Some renderings come
/// back as two or three OCR words ("Tangerine4821" and "Wobble"), and the password rule, which ends a
/// value at the first plain word, masked the first and left the second in the stored text and unpainted
/// in the frame. Which renderings split depends on the glyphs either side of the digits, which is why the
/// loop, drawing a new secret each run, failed now and then (four hosted runs in 72, 2026-11-04 to 08).
/// Before the fix this measured, at 24 pt, 30 splits in 500 renderings and 14 secrets left showing.
///
/// This draws every secret the loop can draw a word pair of, and the same with a lowercase second word
/// that has no capital, ascender or digit (the shortest box a piece can have), the way the test window
/// draws it. It says how many the engine split, how many the redaction left any of showing, and how the
/// gaps sit against <see cref="RedactionEngine.TokenGap"/>, measured by the engine's own rule. It is also
/// the canary for that number: if Windows OCR starts splitting tokens wider apart, this fails and says
/// so. Since the owner's decision of 2026-11-09 a password also masks the rest of its row, so it says
/// too how many renderings that rule alone saved where the token rule would not have. It prints counts
/// and box geometry only, never the text, failing or not (INV-10).
/// </summary>
public sealed class OcrSplitSecretTests
{
    // scripts/windows/demo/common.ps1, $E2eSecretFirstWords and $E2eSecretSecondWords.
    private static readonly string[] FirstWords = ["Tangerine", "Marigold", "Juniper", "Lavender", "Saffron"];
    private static readonly string[] SecondWords = ["Wobble", "Quiver", "Ramble", "Tumble", "Glimmer"];

    // Not the loop's: x-height letters only, so the piece's box is as short as a word's can be. Five or
    // more letters, so the loop's own leak test (a run of five) can see them.
    private static readonly string[] LowercaseSecondWords = ["ocean", "sauce", "canvas", "woven", "summer"];

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Twenty groups in which every digit is once first and once last, so every junction is drawn.</summary>
    private static IEnumerable<string> DigitGroups()
    {
        for (var i = 0; i < 10; i++)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{i}{(i + 3) % 10}{(i + 7) % 10}{(i + 5) % 10}");
            yield return string.Create(CultureInfo.InvariantCulture, $"{(i + 5) % 10}{(i + 1) % 10}{(i + 8) % 10}{i}");
        }
    }

    [Theory]
    [InlineData(24f)] // the test window's label
    [InlineData(14f)]
    [InlineData(11f)] // an ordinary dialog's body text
    [InlineData(9f)] // the smallest text a technician reads
    public async Task TheLoopsSecretIsMaskedWholeHoweverTheEngineSplitsIt(float points)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");
        var engine = new RedactionEngine();

        var expected = FirstWords.Length * (SecondWords.Length + LowercaseSecondWords.Length) * DigitGroups().Count();
        int renders = 0, cueUnread = 0, split = 0, leakedText = 0, leakedPixels = 0, splitApart = 0, spacesJoined = 0;
        int matchMissed = 0, tokenAloneCovers = 0, rowAloneCovers = 0;
        var splitShares = new List<double>();
        var spaceShares = new List<double>();
        var splitMargins = new List<double>(); // pixels a split gap sits under the cutoff
        var spaceMargins = new List<double>(); // pixels a real space sits over it
        var rowShortest = new List<int>();
        var rowTallest = new List<int>();
        var heights = new List<int>();
        foreach (var first in FirstWords)
        {
            foreach (var second in SecondWords.Concat(LowercaseSecondWords))
            {
                foreach (var digits in DigitGroups())
                {
                    var secret = first + digits + second;
                    var words = (await recogniser.ReadAsync(Render(secret, points), TestContext.Current.CancellationToken)).Words;
                    renders++;
                    heights.AddRange(words.Select(w => w.Height));

                    var cue = -1;
                    for (var i = 0; i < words.Count; i++)
                    {
                        if (words[i].Text.StartsWith("pass", StringComparison.OrdinalIgnoreCase))
                        {
                            cue = i;
                            break;
                        }
                    }

                    if (cue < 0)
                    {
                        cueUnread++;
                        continue;
                    }

                    // The secret's pieces: whatever is on the cue's row after it, found on the screen
                    // rather than by where the engine listed it.
                    var pieces = Enumerable.Range(0, words.Count)
                        .Where(i => i != cue && RedactionEngine.SameRow(words[cue], words[i]) && words[i].X > words[cue].X)
                        .OrderBy(i => words[i].X)
                        .ToList();
                    var row = pieces.Select(i => words[i].Height).Append(words[cue].Height).ToList();
                    rowShortest.Add(row.Min());
                    rowTallest.Add(row.Max());

                    if (pieces.Count > 1)
                    {
                        split++;
                        for (var i = 1; i < pieces.Count; i++)
                        {
                            var (share, margin) = Measure(words, pieces[i - 1], pieces[i]);
                            splitShares.Add(share);
                            splitMargins.Add(-margin);
                            splitApart += share > RedactionEngine.TokenGap ? 1 : 0;
                        }
                    }

                    // Every real space on the page: each word and its nearest neighbour to the right on its
                    // row, the pairs inside the secret aside.
                    for (var i = 0; i < words.Count; i++)
                    {
                        if (pieces.Contains(i))
                        {
                            continue;
                        }

                        var right = Enumerable.Range(0, words.Count)
                            .Where(j => j != i && RedactionEngine.SameRow(words[i], words[j]) && words[j].X > words[i].X)
                            .OrderBy(j => words[j].X)
                            .Cast<int?>()
                            .FirstOrDefault();
                        if (right is { } j)
                        {
                            var (share, margin) = Measure(words, i, j);
                            spaceShares.Add(share);
                            spaceMargins.Add(margin);
                            spacesJoined += share <= RedactionEngine.TokenGap ? 1 : 0;
                        }
                    }

                    // What each rule would do alone, where the password match by itself stopped short.
                    if (Missed(words, pieces) is { Missed.Count: > 0 } missed)
                    {
                        matchMissed++;
                        tokenAloneCovers += TokenAloneCovers(words, pieces, missed.Matched, missed.Missed) ? 1 : 0;
                        rowAloneCovers += missed.Matched.Count > 0 && missed.Missed.All(m =>
                            RedactionEngine.SameRow(words[missed.Matched[0]], words[m]) && words[m].X > words[missed.Matched[0]].X) ? 1 : 0;
                    }

                    var redaction = engine.RedactFrame(words);
                    if (Leaks(secret, redaction.Text))
                    {
                        leakedText++;
                    }

                    if (pieces.Any(p => !redaction.Regions.Any(r => Covers(r, words[p]))))
                    {
                        leakedPixels++;
                    }
                }
            }
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"OCR of the loop's secret at {points:0}pt: {renders} of {expected} renderings, the pass cue unread in {cueUnread}, "
            + $"the engine split the secret in **{split}**; left showing in the stored text **{leakedText}**, unpainted in the frame **{leakedPixels}**. "
            + $"Gaps as a share of the row's height: inside a split token {Describe(splitShares)}, at a real space {Describe(spaceShares)}; "
            + $"split gaps wider than the token rule's {RedactionEngine.TokenGap:0.00}: {splitApart}; "
            + $"real spaces it takes for none (over-masking): **{spacesJoined}** of {spaceShares.Count}. "
            + $"The password match alone missed a piece in **{matchMissed}**; the token rule alone covers {tokenAloneCovers} of them, the row rule alone {rowAloneCovers}, "
            + $"so the row rule alone saved **{matchMissed - tokenAloneCovers}** the token rule would not. "
            + $"Margin to the cutoff in pixels: splits at least {MinOrNone(splitMargins)} under, spaces at least {MinOrNone(spaceMargins)} over. "
            + $"Pass row box heights: shortest {Range(rowShortest)} px, tallest {Range(rowTallest)} px; word height median {Median(heights.Select(h => (double)h).ToList()):0} px.");
        Measurements.Record(summary);

        // A probe that read nothing proves nothing: every rendering, the cue in each, and at least one
        // split, or the numbers above vouch for no threshold at all.
        Assert.True(renders == expected, $"Rendered {renders} of {expected}. {summary}");
        Assert.True(cueUnread == 0, $"The engine did not read the pass cue in every rendering, so those were never checked. {summary}");
        Assert.True(split > 0, $"The engine split no secret, so this measured no split gap. {summary}");
        Assert.True(splitApart == 0, $"Windows OCR changed: re-measure TokenGap. A split token's pieces sat further apart than the rule joins. {summary}");
        Assert.True(leakedText == 0 && leakedPixels == 0, $"The secret was left showing. {summary}");
    }

    /// <summary>
    /// The secret's pieces the password match itself touches, and those it does not, found by running the
    /// pattern library over the page text the way the engine builds it.
    /// </summary>
    private static (List<int> Matched, List<int> Missed) Missed(IReadOnlyList<OcrWord> words, List<int> pieces)
    {
        var spans = new List<(int Start, int End)>(words.Count);
        var text = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (text.Length > 0)
            {
                _ = text.Append(' ');
            }

            var start = text.Length;
            _ = text.Append(word.Text);
            spans.Add((start, text.Length));
        }

        var matches = PatternLibrary.Find(text.ToString(), RedactionPolicy.Default, () => { })
            .Where(m => m.Kind == MaskKind.Password)
            .ToList();
        var matched = pieces.Where(p => matches.Any(m => m.Start < spans[p].End && spans[p].Start < m.End)).ToList();
        return (matched, [.. pieces.Except(matched)]);
    }

    /// <summary>Whether every missed piece is reached from a matched one through gaps the token rule joins.</summary>
    private static bool TokenAloneCovers(IReadOnlyList<OcrWord> words, List<int> pieces, List<int> matched, List<int> missed)
    {
        var reached = new HashSet<int>(matched);
        var waiting = new Queue<int>(matched);
        while (waiting.TryDequeue(out var i))
        {
            var row = RedactionEngine.RowHeight(words, words[i]);
            foreach (var j in pieces)
            {
                if (!reached.Contains(j) && RedactionEngine.GapShare(words[i], words[j], row) is <= RedactionEngine.TokenGap)
                {
                    _ = reached.Add(j);
                    waiting.Enqueue(j);
                }
            }
        }

        return missed.All(reached.Contains);
    }

    /// <summary>The engine's own share for the pair, and how many pixels the gap sits over the cutoff (negative under).</summary>
    private static (double Share, double Margin) Measure(IReadOnlyList<OcrWord> words, int a, int b)
    {
        var row = RedactionEngine.RowHeight(words, words[a]);
        var share = RedactionEngine.GapShare(words[a], words[b], row) ?? double.NaN;
        var height = Math.Max(row, Math.Max(words[a].Height, words[b].Height));
        return (share, (share - RedactionEngine.TokenGap) * height);
    }

    private static bool Covers(MaskedRegion region, OcrWord word) =>
        region.X <= word.X && region.Y <= word.Y
        && region.X + region.Width >= word.X + word.Width
        && region.Y + region.Height >= word.Y + word.Height;

    /// <summary>The loop's own test: the whole secret, or any run of five or more of its letters.</summary>
    private static bool Leaks(string secret, string text) =>
        text.Contains(secret, StringComparison.OrdinalIgnoreCase)
        || Regex.Matches(secret, "[A-Za-z]{5,}").Any(m => text.Contains(m.Value, StringComparison.OrdinalIgnoreCase));

    private static string Describe(List<double> values) =>
        values.Count == 0
            ? "none"
            : string.Create(CultureInfo.InvariantCulture, $"{values.Min():0.00} to {values.Max():0.00} (median {Median(values):0.00}, {values.Count})");

    private static string MinOrNone(List<double> values) =>
        values.Count == 0 ? "n/a" : values.Min().ToString("0.0", CultureInfo.InvariantCulture);

    private static string Range(List<int> values) =>
        values.Count == 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{values.Min()} to {values.Max()}");

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// The test window's first screen (client/ScreenTail.E2E/TestWindow.cs): a Windows Forms label, so
    /// GDI's text renderer, black Segoe UI on white with 24 px of padding, encoded as the capturer
    /// encodes a frame.
    /// </summary>
    private static byte[] Render(string secret, float points)
    {
        var text = string.Join(
            Environment.NewLine,
            "Front desk printer queue checklist",
            "Ticket 48213 reception printer offline",
            $"pass: {secret}",
            "Queue cleared and a test page sent");

        using var bitmap = new Bitmap(924, 421, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var font = new Font("Segoe UI", points, FontStyle.Regular, GraphicsUnit.Point);
            System.Windows.Forms.TextRenderer.DrawText(
                graphics,
                text,
                font,
                new Rectangle(24, 24, bitmap.Width - 48, bitmap.Height - 48),
                Color.Black,
                Color.White,
                System.Windows.Forms.TextFormatFlags.WordBreak | System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.Top);
        }

        using var parameters = new EncoderParameters(1);
        using var quality = new EncoderParameter(Encoder.Quality, 82L);
        parameters.Param[0] = quality;
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, JpegCodec, parameters);
        return buffer.ToArray();
    }
}
