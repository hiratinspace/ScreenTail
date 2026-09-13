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
        // The measurement this ticket is really for, taken at two sizes.
        //
        // One size cannot answer the question. The first run of this measured a 420x220 window at 20 ms and
        // divided by its area, which said "217 ms per megapixel" and implied 1.8 seconds for a 4K frame.
        // That number was an artifact: almost all of those 20 ms were fixed cost, and dividing fixed cost by
        // a tiny area inflates it without limit. Two points separate the constant from the slope, and the
        // slope is what a 4K frame actually costs.
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var capturer = new ScreenshotCapturer();

        var small = MeasureAt(capturer, 420, 220);
        var large = MeasureAt(capturer, 0, 0);
        Assert.SkipWhen(small is null || large is null, "Could not take the foreground.");

        var smallPixels = small!.SourceWidth * (double)small.SourceHeight;
        var largePixels = large!.SourceWidth * (double)large.SourceHeight;
        Assert.SkipWhen(largePixels <= smallPixels * 4, "The two windows are too close in size to separate fixed from per-pixel cost.");

        // total = fixed + slope x pixels, solved from the two measurements.
        var slopePerPixel = (large.Timing.Total.TotalMilliseconds - small.Timing.Total.TotalMilliseconds) / (largePixels - smallPixels);
        var fixedCost = small.Timing.Total.TotalMilliseconds - (slopePerPixel * smallPixels);
        var fourK = fixedCost + (slopePerPixel * 3840 * 2160);

        Measurements.Record($"Capture {small.SourceWidth}x{small.SourceHeight}: {small.Timing}");
        Measurements.Record($"Capture {large.SourceWidth}x{large.SourceHeight}: {large.Timing}");
        Measurements.Record(
            $"Capture cost: **{fixedCost:F1} ms fixed** + **{slopePerPixel * 1_000_000:F1} ms/megapixel** → a 4K frame would be about **{fourK:F0} ms** against a 120 ms budget");

        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");

        // The real budget is 4K, which this display is not, so what is enforced here is the thing that
        // would make 4K hopeless: a fixed cost already eating the whole budget before a pixel is copied.
        Assert.True(fixedCost < 120, $"fixed cost alone is {fixedCost:F1} ms, and the whole 4K budget is 120 ms");
    }

    /// <summary>Captures one window of the requested size, or the whole screen when given zero.</summary>
    private static CapturedFrame? MeasureAt(ScreenshotCapturer capturer, int width, int height)
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

        // Warm first: the first capture at a new size allocates the bitmap, and that cost belongs to
        // start-up rather than to every click.
        capturer.CaptureForegroundWindow();
        var frames = Enumerable.Range(0, 10).Select(_ => capturer.CaptureForegroundWindow()).Where(f => f is not null).ToList();
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
