using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// ST-041 against the real OCR engine. This is where ADR-0001's last open risk gets settled: Tesseract
/// measured 1.19 s per frame against a 700 ms budget, and the ADR listed Windows.Media.Ocr as one of three
/// ways out. These tests say whether it reads a settings dialog well enough to redact one, and how long it
/// takes on the machine a technician would actually use.
/// </summary>
public sealed class OcrTests
{
    /// <summary>
    /// This project targets Windows but the solution's tests also run on the Mac dev box, where these
    /// types throw on construction rather than politely failing. The OS check has to come before anything
    /// touches WinRT or GDI+, which is why it is the first line of every test here.
    /// </summary>
    private static bool OnWindows => OperatingSystem.IsWindows();

    private static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);

    /// <summary>The labels a Windows settings dialog puts on screen — what a technician screenshots all day.</summary>
    private static readonly string[] DialogLabels =
    [
        "Print Spooler", "Properties", "General", "Log On", "Recovery", "Dependencies",
        "Service name", "Display name", "Startup type", "Automatic", "Service status", "Stopped",
        "Start", "Stop", "Pause", "Resume", "Apply", "Cancel",
    ];

    [Fact]
    public async Task TheEngineReadsASettingsDialog()
    {
        // The acceptance criterion: a 1080p settings dialog, at least 90% of its labels recognised.
        Assert.SkipUnless(OnWindows, "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");
        var image = RenderDialog(1920, 1080);

        var text = await recogniser.ReadAsync(image, TestContext.Current.CancellationToken);

        var read = string.Join(' ', text.Words.Select(w => w.Text));
        var found = DialogLabels.Count(label => read.Contains(label, StringComparison.OrdinalIgnoreCase));
        var rate = found / (double)DialogLabels.Length;

        Measurements.Save("ocr-dialog.jpg", image);
        Measurements.Record($"OCR read **{found}/{DialogLabels.Length}** dialog labels ({rate:P0}) in {recogniser.Language}");
        Assert.True(rate >= 0.9, $"only {rate:P0} of the labels were recognised; the redaction engine can only mask text it can read");
    }

    [Fact]
    public async Task TheEngineFindsASecretWhereItSitsOnScreen()
    {
        // What the whole pipeline depends on: not just reading the text, but knowing where it is, because
        // a box in the wrong place paints over the wrong pixels.
        Assert.SkipUnless(OnWindows, "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");
        var image = RenderDialog(1920, 1080, secret: "4111 1111 1111 1111");

        Measurements.Save("ocr-secret.jpg", image);
        var text = await recogniser.ReadAsync(image, TestContext.Current.CancellationToken);
        var redaction = new RedactionEngine().RedactFrame(text.Words);

        // What the engine actually read, in the failure message and in the run's measurements: a card that
        // is not detected is either a pattern problem or a recognition problem, and only the words say which.
        var digits = string.Join(' ', text.Words.Select(w => w.Text).Where(w => w.Any(char.IsDigit)));
        Measurements.Record($"OCR read these digit groups: `{digits}`");
        Assert.True(
            redaction.Counts.ContainsKey(MaskKind.Card),
            $"no card was found; OCR read the digits as: '{digits}' and the full text as: '{redaction.Text}'");
        Assert.DoesNotContain("4111", redaction.Text, StringComparison.Ordinal);

        // Where the engine saw it matters as much as whether. A box in the wrong place paints the wrong
        // pixels, so the masked region has to line up with one of the two places it was drawn.
        var regions = redaction.Regions.Where(r => r.Kind == MaskKind.Card).ToList();
        Assert.All(regions, r => Assert.True(r.Width > 100, $"a {r.Width}px box for a 19-character number is too narrow"));
        Assert.Contains(regions, r => r.Y is > 600 and < 760 || r.Y is > 250 and < 400);

        // Both placements, or only the one inside the column? The answer decides whether an isolated
        // password in a sparse dialog is something this pipeline can see at all.
        Measurements.Record(
            $"Card drawn in the label column and alone in empty space: the engine found **{regions.Count}** of the 2 "
            + $"(at y = {string.Join(", ", regions.Select(r => r.Y))})");
    }

    [Fact]
    public async Task WhatOcrCostsPerFrame()
    {
        // ADR-0001's open question, answered on the hardware that matters.
        Assert.SkipUnless(OnWindows, "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");
        var native = RenderDialog(1920, 1080);

        await recogniser.ReadAsync(native, TestContext.Current.CancellationToken);   // warm the engine

        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await recogniser.ReadAsync(native, TestContext.Current.CancellationToken);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        Measurements.Record(
            $"OCR on a 1920x1080 frame: median **{median:F0} ms**, worst {times[^1]:F0} ms (budget 700 ms, "
            + $"Tesseract measured 1190 ms in ADR-0001, enforced: {PerformanceCounts})");

        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");
        Assert.True(median < 700, $"OCR took a median of {median:F0} ms against a 700 ms budget");
    }

    [Fact]
    public async Task ALoginPromptIsRecognisedFromWhatIsOnScreen()
    {
        // Inside a remote session this is the only signal there is: the desktop is one opaque bitmap and
        // UI Automation cannot see the password box (ADR-0001 finding 2).
        Assert.SkipUnless(OnWindows, "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");
        var image = RenderLoginPrompt();

        var text = await recogniser.ReadAsync(image, TestContext.Current.CancellationToken);

        Measurements.Save("ocr-login.jpg", image);
        Assert.True(
            LoginScreenHeuristic.LooksLikeLogin(text.Words),
            $"a sign-in prompt was not recognised; OCR read: {string.Join(' ', text.Words.Select(w => w.Text))}");
    }

    [Fact]
    public void MaskingCoversTheRegionAndThenShrinks()
    {
        // The ordering that makes masking correct: paint at native size, downscale afterwards.
        Assert.SkipUnless(OnWindows, "Windows only.");
        var image = RenderDialog(1920, 1080, secret: "4111 1111 1111 1111");
        var masker = new WindowsFrameMasker();

        var masked = masker.Mask(
            image,
            [new MaskedRegion { X = 100, Y = 500, Width = 400, Height = 40, Kind = MaskKind.Card }],
            maxEdge: 1600);

        Assert.True(Math.Max(masked.Width, masked.Height) <= 1600);
        Assert.Equal(1600, masked.Width);

        // The painted area is black in the stored image, at the scaled-down coordinates.
        using var stored = new Bitmap(new MemoryStream(masked.Image));
        var scale = 1600.0 / 1920;
        var sample = stored.GetPixel((int)(300 * scale), (int)(520 * scale));
        Assert.True(sample.R < 40 && sample.G < 40 && sample.B < 40, $"expected the masked area to be black, found {sample}");
        Measurements.Save("masked-frame.jpg", masked.Image);
    }

    /// <summary>A settings dialog, drawn rather than captured, so the same pixels are read on every runner.</summary>
    private static byte[] RenderDialog(int width, int height, string? secret = null)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(243, 243, 243));
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var heading = new Font("Segoe UI", 16, FontStyle.Bold);
        using var body = new Font("Segoe UI", 11);
        using var ink = new SolidBrush(Color.FromArgb(28, 28, 28));

        graphics.DrawString("Print Spooler Properties", heading, ink, 60, 40);
        var y = 110;
        foreach (var label in DialogLabels.Skip(1))
        {
            graphics.DrawString(label, body, ink, 100, y);
            y += 34;
        }

        if (secret is not null)
        {
            // Twice, deliberately. Once at the foot of the column of labels, which is what a card number in
            // a real dialog looks like, and once alone in the empty right-hand half. The first run of this
            // drew it only in the empty half and the engine returned no digits at all, so the two
            // placements are kept apart to show whether that was about the text or about its surroundings.
            graphics.DrawString(secret, body, ink, 100, y + 34);
            graphics.DrawString(secret, body, ink, 900, 300);
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }

    private static byte[] RenderLoginPrompt()
    {
        using var bitmap = new Bitmap(900, 600, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(243, 243, 243));
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var heading = new Font("Segoe UI", 18, FontStyle.Bold);
        using var body = new Font("Segoe UI", 12);
        using var ink = new SolidBrush(Color.FromArgb(28, 28, 28));

        graphics.DrawString("Windows Security", heading, ink, 80, 60);
        graphics.DrawString("Sign in to continue", body, ink, 80, 120);
        graphics.DrawString("Username", body, ink, 80, 200);
        graphics.DrawString("Password", body, ink, 80, 280);
        graphics.DrawString("Remember me", body, ink, 80, 360);
        graphics.DrawString("OK", body, ink, 80, 440);

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }
}
