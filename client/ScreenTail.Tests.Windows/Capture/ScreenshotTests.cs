using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Capture;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Capture;

namespace ScreenTail.Tests.Windows.Capture;

/// <summary>
/// ST-025 against a real screen. The number this exists to produce is the per-stage timing: ADR-0001
/// measured 178 ms p95 for this whole path against a 120 ms budget but recorded only the total, and a total
/// does not say what to fix. A slow grab argues for Windows.Graphics.Capture; a slow encode argues for WIC.
/// </summary>
public sealed class ScreenshotTests
{
    private static bool CanCapture =>
        new WindowsCapabilityProbe().Probe()[Capability.ScreenCapture].State == CapabilityState.Ok
        && new WindowsCapabilityProbe().Probe()[Capability.DesktopSession].State == CapabilityState.Ok;

    private static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AWindowIsCapturedAtItsOwnSizeAndStoredSmaller()
    {
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var window = DesktopWindow.Create("ScreenTail capture target");
        Assert.SkipUnless(window.TakeForeground(), "Could not take the foreground.");

        using var capturer = new ScreenshotCapturer();
        var frame = capturer.CaptureForegroundWindow();

        Assert.NotNull(frame);
        Assert.True(frame.SourceWidth > 0 && frame.SourceHeight > 0);
        Assert.True(Math.Max(frame.Width, frame.Height) <= Downscale.MaxEdge);
        Assert.True(frame.Image.Length > 0);

        // A JPEG, not an empty buffer that happens to have length: SOI marker then the JFIF/EXIF app block.
        Assert.Equal(0xFF, frame.Image[0]);
        Assert.Equal(0xD8, frame.Image[1]);
    }

    [Fact]
    public void TheCaptureIsNotBlank()
    {
        // A capture from a session without a desktop comes back as a uniform black rectangle that encodes
        // to a tiny JPEG. Size is a crude but reliable way to tell "captured nothing" from "captured".
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var window = DesktopWindow.Create("ScreenTail blankness target");
        Assert.SkipUnless(window.TakeForeground(), "Could not take the foreground.");

        using var capturer = new ScreenshotCapturer();
        var frame = capturer.CaptureForegroundWindow();

        Assert.NotNull(frame);
        Assert.True(frame.Image.Length > 2_000, $"the frame encoded to {frame.Image.Length} bytes, which is what a blank capture looks like");
    }

    [Fact]
    public void WhatEachStageOfCaptureCosts()
    {
        // The measurement this ticket is really for, and it took three attempts to make it mean anything.
        //
        // One window size divided total time by a tiny area and claimed 217 ms per megapixel. Two sizes
        // fixed that but still charged a 4K frame for an 8.3-megapixel encode, which never happens: the
        // stored image is capped at 1600 px, so grab and convert scale with the source while encode scales
        // with what is kept. A third measurement, taken with the cap lowered so a resize actually occurs,
        // gives the resize its own slope. Only then does "what would 4K cost" mean something.
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var capturer = new ScreenshotCapturer();

        var small = MeasureAt(capturer, 420, 220, Downscale.MaxEdge);
        var large = MeasureAt(capturer, 0, 0, Downscale.MaxEdge);
        var resized = MeasureAt(capturer, 0, 0, maxEdge: 640);
        Assert.SkipWhen(small is null || large is null || resized is null, "Could not take the foreground.");

        var smallMp = small!.SourceWidth * (double)small.SourceHeight / 1_000_000;
        var largeMp = large!.SourceWidth * (double)large.SourceHeight / 1_000_000;
        Assert.SkipWhen(largeMp <= smallMp * 4, "The two windows are too close in size to separate fixed cost from slope.");

        // Per stage, because the stages scale against different things.
        var grabSlope = Slope(small.Timing.Grab, large.Timing.Grab, smallMp, largeMp);
        var grabFixed = small.Timing.Grab.TotalMilliseconds - (grabSlope * smallMp);
        var convertSlope = Slope(small.Timing.Convert, large.Timing.Convert, smallMp, largeMp);
        var encodeSlope = Slope(small.Timing.Encode, large.Timing.Encode, smallMp, largeMp);
        var resizeSlope = resized!.Timing.Resize.TotalMilliseconds / largeMp;

        const double FourKMp = 3840 * 2160 / 1_000_000.0;
        var storedMp = Downscale.For(3840, 2160) is var plan ? plan.Width * (double)plan.Height / 1_000_000 : 0;
        var fourK = grabFixed + (grabSlope * FourKMp) + (convertSlope * FourKMp) + (resizeSlope * FourKMp) + (encodeSlope * storedMp);

        Measurements.Record($"Capture {small.SourceWidth}x{small.SourceHeight}: {small.Timing}");
        Measurements.Record($"Capture {large.SourceWidth}x{large.SourceHeight}: {large.Timing}");
        Measurements.Record($"Capture {resized.SourceWidth}x{resized.SourceHeight} → {resized.Width}x{resized.Height}: {resized.Timing}");
        Measurements.Record(
            $"Per stage: grab **{grabFixed:F1} ms fixed + {grabSlope:F1} ms/MP**, convert {convertSlope:F1} ms/MP, "
            + $"resize {resizeSlope:F1} ms/MP, encode {encodeSlope:F1} ms/MP of stored");
        Measurements.Record(
            $"A 4K frame (8.3 MP → {plan.Width}x{plan.Height} stored) would cost about **{fourK:F0} ms** against a 120 ms budget");

        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");

        // What a 1080p window costs is enforceable on this hardware; 4K is reported, because this display
        // is not 4K and asserting an extrapolation would be asserting arithmetic rather than a machine.
        Assert.True(
            large.Timing.Total.TotalMilliseconds < 120,
            $"a {largeMp:F1} MP window took {large.Timing.Total.TotalMilliseconds:F0} ms ({large.Timing})");
    }

    private static double Slope(TimeSpan small, TimeSpan large, double smallMp, double largeMp) =>
        (large.TotalMilliseconds - small.TotalMilliseconds) / (largeMp - smallMp);

    /// <summary>Captures one window of the requested size, or the whole screen when given zero.</summary>
    private static CapturedFrame? MeasureAt(ScreenshotCapturer capturer, int width, int height, int maxEdge)
    {
        using var window = DesktopWindow.Create($"ScreenTail timing {width}x{height}");
        if (!window.TakeForeground())
        {
            return null;
        }

        if (width > 0)
        {
            window.Resize(width, height);
        }
        else
        {
            window.Maximize();
        }

        Thread.Sleep(150);

        // Warm first: the first capture at a new size allocates the bitmap, and that belongs to start-up
        // rather than to every click.
        capturer.CaptureForegroundWindow(maxEdge);
        var frames = Enumerable.Range(0, 10).Select(_ => capturer.CaptureForegroundWindow(maxEdge)).Where(f => f is not null).ToList();
        return frames.Count == 0 ? null : frames.MaxBy(f => f!.Timing.Total);
    }

    [Fact]
    public void ASmallWindowIsNotUpscaled()
    {
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var window = DesktopWindow.Create("ScreenTail small target");
        Assert.SkipUnless(window.TakeForeground(), "Could not take the foreground.");

        using var capturer = new ScreenshotCapturer();
        var frame = capturer.CaptureForegroundWindow();

        Assert.NotNull(frame);
        if (Math.Max(frame.SourceWidth, frame.SourceHeight) <= Downscale.MaxEdge)
        {
            Assert.Equal(frame.SourceWidth, frame.Width);
            Assert.Equal(frame.SourceHeight, frame.Height);
        }
    }

    [Fact]
    public void CapturingRepeatedlyDoesNotLeakHandles()
    {
        // Every capture creates a DC, a bitmap and a GDI object. Leaking any of them exhausts the desktop
        // heap within a working day, and the symptom would be windows failing to draw rather than a crash here.
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var window = DesktopWindow.Create("ScreenTail handle target");
        Assert.SkipUnless(window.TakeForeground(), "Could not take the foreground.");
        using var capturer = new ScreenshotCapturer();

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        for (var i = 0; i < 5; i++)
        {
            capturer.CaptureForegroundWindow();
        }

        self.Refresh();
        var before = self.HandleCount;
        for (var i = 0; i < 50; i++)
        {
            capturer.CaptureForegroundWindow();
        }

        self.Refresh();
        var growth = self.HandleCount - before;
        Assert.True(growth < 50, $"handle count grew by {growth} over 50 captures");
    }
}
