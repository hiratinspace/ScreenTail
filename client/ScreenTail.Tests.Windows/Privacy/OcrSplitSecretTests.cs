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
/// loop, drawing a new secret each run, failed about one run in seven.
///
/// This draws every secret the loop can draw a word pair of, the way the test window draws it, and says
/// how many the engine split and how many the redaction left any of showing. It prints counts and box
/// geometry only, never the text (INV-10).
/// </summary>
public sealed class OcrSplitSecretTests
{
    // scripts/windows/demo/common.ps1, $E2eSecretFirstWords and $E2eSecretSecondWords.
    private static readonly string[] FirstWords = ["Tangerine", "Marigold", "Juniper", "Lavender", "Saffron"];
    private static readonly string[] SecondWords = ["Wobble", "Quiver", "Ramble", "Tumble", "Glimmer"];

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
    [InlineData(11f)] // an ordinary dialog's body text
    public async Task TheLoopsSecretIsMaskedWholeHoweverTheEngineSplitsIt(float points)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");
        var engine = new RedactionEngine();

        int renders = 0, cueUnread = 0, split = 0, leakedText = 0, leakedPixels = 0;
        var splitGaps = new List<double>();
        var spaceGaps = new List<double>();
        var heights = new List<int>();
        foreach (var first in FirstWords)
        {
            foreach (var second in SecondWords)
            {
                foreach (var digits in DigitGroups())
                {
                    var secret = first + digits + second;
                    var image = Render(secret, points);
                    var words = (await recogniser.ReadAsync(image, TestContext.Current.CancellationToken)).Words;
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

                    // Every real space on the page, the pass line's included, for the geometry.
                    for (var i = 1; i < words.Count; i++)
                    {
                        if (SameLine(words[i - 1], words[i]) && (cue < 0 || i <= cue + 1 || !SameLine(words[cue], words[i])))
                        {
                            spaceGaps.Add(Gap(words[i - 1], words[i]));
                        }
                    }

                    if (cue < 0)
                    {
                        cueUnread++;
                        continue;
                    }

                    var pieces = words.Skip(cue + 1).TakeWhile(w => SameLine(words[cue], w)).ToList();
                    if (pieces.Count > 1)
                    {
                        split++;
                        for (var i = 1; i < pieces.Count; i++)
                        {
                            splitGaps.Add(Gap(pieces[i - 1], pieces[i]));
                        }
                    }

                    var redaction = engine.RedactFrame(words);
                    if (Leaks(secret, redaction.Text))
                    {
                        leakedText++;
                    }

                    if (pieces.Any(p => !redaction.Regions.Any(r => Covers(r, p))))
                    {
                        leakedPixels++;
                    }
                }
            }
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"OCR of the loop's secret at {points:0}pt: {renders} renderings, the engine split the secret in **{split}**, "
            + $"the pass cue unread in {cueUnread}; left showing in the stored text **{leakedText}**, unpainted in the frame **{leakedPixels}**. "
            + $"Gaps as a share of word height: inside a split token {Describe(splitGaps)}, at a real space {Describe(spaceGaps)}; "
            + $"word height median {Median(heights.Select(h => (double)h).ToList()):0} px.");
        Measurements.Record(summary);

        Assert.True(leakedText == 0 && leakedPixels == 0, summary);
    }

    private static bool SameLine(OcrWord a, OcrWord b)
    {
        var middle = b.Y + (b.Height / 2.0);
        return middle >= a.Y && middle <= a.Y + a.Height && b.X >= a.X;
    }

    private static double Gap(OcrWord a, OcrWord b) =>
        (b.X - (a.X + a.Width)) / (double)Math.Max(1, Math.Min(a.Height, b.Height));

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
