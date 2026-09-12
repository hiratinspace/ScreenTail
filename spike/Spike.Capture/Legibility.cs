using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using ScreenTail.Spike.Core;
using Tesseract;

namespace ScreenTail.Spike.Capture;

/// <summary>
/// AC4: renders 4K frames full of 9 pt UI text at common display scales, downscales to ≤ 1600 px,
/// and reports JPEG sizes plus how many words OCR still reads. Saves the q80 JPEGs for eyeballing.
/// </summary>
internal static class Legibility
{
    private static readonly string[] DialogLines =
    [
        "Print Spooler Properties (Local Computer)",
        "General   Log On   Recovery   Dependencies",
        "Service name: Spooler",
        "Display name: Print Spooler",
        "Path to executable: C:\\Windows\\System32\\spoolsv.exe",
        "Startup type: Automatic",
        "Service status: Stopped",
        "Event ID 7031: The Print Spooler service terminated unexpectedly",
        "IPv4 Address . . . . . . . . . . : 10.0.12.44",
        "Default Gateway . . . . . . . . : 10.0.12.1",
        "Group Policy Management Editor - Default Domain Policy",
        "Computer Configuration > Policies > Windows Settings",
        "User must change password at next logon",
        "Account is disabled   Password never expires",
        "OK   Cancel   Apply",
    ];

    private static readonly double[] DisplayScales = [1.0, 1.25, 1.5, 2.0];
    private static readonly long[] Qualities = [70, 80, 90];

    public static async Task<int> ExecuteAsync(SpikeOptions options, string outDir)
    {
        using var engine = options.Ocr ? await OcrWorker.TryCreateEngineAsync(_ => { }) : null;

        var table = new StringBuilder();
        table.AppendLine("# ST-001 legibility: 4K frame downscaled to <= 1600 px");
        table.AppendLine();
        table.AppendLine("Recall = share of ground-truth words Tesseract reads back. For the screen capture, the native-resolution OCR is the reference.");
        table.AppendLine("Open the saved `legibility-*-q80.jpg` files at 100% zoom to judge legibility by eye as well.");
        table.AppendLine();
        table.AppendLine("| Source | Display scale | 9 pt text height native -> out (px) | Out size | JPEG q70 / q80 / q90 (KB) | Recall native | Recall out q80 |");
        table.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var scale in DisplayScales)
        {
            Console.WriteLine($"Synthetic 3840x2160 at {scale:0.##}x...");
            var (frame, truth) = RenderSyntheticDialog(3840, 2160, scale);
            using (frame)
            {
                Measure(frame, "synthetic 3840x2160", scale, truth, engine, outDir, table);
            }
        }

        if (options.FromScreen)
        {
            var bounds = ScreenGrab.PrimaryScreen();
            Console.WriteLine($"Primary screen {bounds.Width}x{bounds.Height}...");
            using var frame = ScreenGrab.Capture(bounds);
            Measure(frame, $"primary screen {bounds.Width}x{bounds.Height}", Native.GetDpiForSystem() / 96.0, null, engine, outDir, table);
        }

        var path = Path.Combine(outDir, "legibility.md");
        await File.WriteAllTextAsync(path, table.ToString());
        Console.WriteLine($"Legibility report: {path}");
        return 0;
    }

    private static void Measure(
        Bitmap native,
        string source,
        double scale,
        string? truth,
        TesseractEngine? engine,
        string outDir,
        StringBuilder table)
    {
        using var small = ScreenGrab.Downscale(native);
        var factor = (double)small.Width / native.Width;
        var encoded = Qualities.Select(q => ScreenGrab.EncodeJpeg(small, q)).ToArray();
        var q80 = encoded[Array.IndexOf(Qualities, 80L)];

        var slug = string.Create(CultureInfo.InvariantCulture, $"{(truth is null ? "screen" : "synthetic")}-{scale * 100:0}");
        File.WriteAllBytes(Path.Combine(outDir, $"legibility-{slug}-q80.jpg"), q80);

        string recallNative = "n/a", recallOut = "n/a";
        if (engine is not null)
        {
            var nativeText = OcrWorker.Recognize(engine, ScreenGrab.EncodePng(native)).Text;
            var outText = OcrWorker.Recognize(engine, q80).Text;
            recallNative = truth is null ? "(reference)" : Percent(OcrAgreement.WordRecall(truth, nativeText));
            recallOut = Percent(OcrAgreement.WordRecall(truth ?? nativeText, outText));
        }

        table.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {source} | {scale:0.##}x | {DownscalePlan.TextPixelHeight(9, scale, 1):0.#} -> {DownscalePlan.TextPixelHeight(9, scale, factor):0.#} | {small.Width}x{small.Height} | {Kb(encoded[0])} / {Kb(encoded[1])} / {Kb(encoded[2])} | {recallNative} | {recallOut} |"));
    }

    private static (Bitmap Frame, string Truth) RenderSyntheticDialog(int width, int height, double scale)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(0xF0, 0xF0, 0xF0));
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var emPixels = (float)DownscalePlan.TextPixelHeight(9, scale, 1);
        using var font = new Font("Segoe UI", emPixels, GraphicsUnit.Pixel);
        var lineHeight = (int)Math.Ceiling(emPixels * 1.6);
        var columnWidth = (int)Math.Ceiling(emPixels * 40);

        var truth = new StringBuilder();
        var index = 0;
        for (var x = 24; x + columnWidth <= width; x += columnWidth + 24)
        {
            for (var y = 24; y + lineHeight <= height; y += lineHeight)
            {
                var line = DialogLines[index++ % DialogLines.Length];
                graphics.DrawString(line, font, Brushes.Black, x, y);
                truth.AppendLine(line);
            }
        }

        return (bitmap, truth.ToString());
    }

    private static string Kb(byte[] bytes) => (bytes.Length / 1024.0).ToString("0", CultureInfo.InvariantCulture);

    private static string Percent(double value) => value.ToString("P0", CultureInfo.InvariantCulture);
}
