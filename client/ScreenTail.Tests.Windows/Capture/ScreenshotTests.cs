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
    private static bool PerformanceCounts => Hardware.PerformanceCounts;

    [Fact]
    public void AWindowIsCapturedAtItsOwnSizeAndStoredSmaller()
    {
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail capture target");
        window.RequireForeground();

        using var capturer = new ScreenshotCapturer();
        var frame = capturer.CaptureForegroundWindow();

        Assert.NotNull(frame);
        Assert.True(frame.SourceWidth > 0 && frame.SourceHeight > 0);

        // Staged at the size it was captured: the worker downscales after it has read the text.
        Assert.Equal(frame.SourceWidth, frame.Width);
        Assert.Equal(frame.SourceHeight, frame.Height);
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
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail blankness target");
        window.RequireForeground();

        using var capturer = new ScreenshotCapturer();
        var frame = capturer.CaptureForegroundWindow();

        Assert.NotNull(frame);
        Assert.True(frame.Image.Length > 2_000, $"the frame encoded to {frame.Image.Length} bytes, which is what a blank capture looks like");
    }

    [Fact]
    public void WhatEachStageOfCaptureCosts()
    {
        // Frames are staged at native resolution (ADR-0001 finding 2a), so this measures what a click
        // costs: grab, convert, encode, all at the window's own size. The downscale belongs to the
        // redaction worker and is measured there.
        Hardware.RequireScreenCapture();
        using var capturer = new ScreenshotCapturer();

        var small = MeasureAt(capturer, 420, 220);
        var large = MeasureAt(capturer, 0, 0);
        Assert.SkipWhen(small is null || large is null, "Could not take the foreground.");

        var smallMp = small!.SourceWidth * (double)small.SourceHeight / 1_000_000;
        var largeMp = large!.SourceWidth * (double)large.SourceHeight / 1_000_000;
        Assert.SkipWhen(largeMp <= smallMp * 4, "The two windows are too close in size to separate fixed cost from slope.");

        var slope = Slope(small.Timing.Total, large.Timing.Total, smallMp, largeMp);
        var fixedCost = small.Timing.Total.TotalMilliseconds - (slope * smallMp);
        const double FourKMp = 3840 * 2160 / 1_000_000.0;

        Measurements.Save("capture-native.jpg", large.Image);
        Measurements.Record($"Capture {small.SourceWidth}x{small.SourceHeight} native: {small.Timing}");
        Measurements.Record($"Capture {large.SourceWidth}x{large.SourceHeight} native: {large.Timing}");
        Measurements.Record(
            $"Staging a native frame: **{fixedCost:F1} ms fixed + {slope:F1} ms/MP** → a 4K frame about "
            + $"**{fixedCost + (slope * FourKMp):F0} ms** against a 120 ms budget");

        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");
        Assert.True(
            large.Timing.Total.TotalMilliseconds < 120,
            $"a {largeMp:F1} MP window took {large.Timing.Total.TotalMilliseconds:F0} ms ({large.Timing})");
    }

    private static double Slope(TimeSpan small, TimeSpan large, double smallMp, double largeMp) =>
        (large.TotalMilliseconds - small.TotalMilliseconds) / (largeMp - smallMp);

    /// <summary>Captures one window of the requested size, or the whole screen when given zero.</summary>
    private static CapturedFrame? MeasureAt(ScreenshotCapturer capturer, int width, int height)
    {
        using var window = DesktopWindow.Create($"ScreenTail timing {width}x{height}");
        if (!window.TakeForeground())
        {
            // The caller turns this into a skip, so the marker has to be left here — see
            // DesktopWindow.RequireForeground for why a silent skip on this runner is a defect.
            Measurements.Record(
                $"{DesktopWindow.NoDesktopMarker}: a test window could not take the foreground, so every "
                + "check that needs one was skipped. The laptop's session is locked, asleep, or signed out.");
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
        capturer.CaptureForegroundWindow();
        var frames = Enumerable.Range(0, 10).Select(_ => capturer.CaptureForegroundWindow()).Where(f => f is not null).ToList();
        return frames.Count == 0 ? null : frames.MaxBy(f => f!.Timing.Total);
    }

    [Fact]
    public void NothingIsCapturedWhenTheForegroundHasMovedOn()
    {
        // INV-5, against the real screen. The scope decision is made on one task when the foreground
        // changes; the click loop drains up to 50 ms later and the scene sampler ticks every second. A
        // technician who clicks in the remote session and immediately alt-tabs would otherwise have the
        // window they moved to photographed against a decision about the one they left.
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail scope target");
        window.RequireForeground();
        using var capturer = new ScreenshotCapturer();

        // A handle that is real but is not the foreground: this window's own child would do, and so does
        // a handle that no longer exists. The point is that it is not what GetForegroundWindow returns.
        var frame = capturer.CaptureForegroundWindow(expected: window.Handle + 1);

        Assert.Null(frame);
        Assert.NotNull(capturer.CaptureForegroundWindow(expected: window.Handle));
    }

    [Fact]
    public void CapturingRepeatedlyDoesNotLeakHandles()
    {
        // Every capture creates a DC, a bitmap and a GDI object. Leaking any of them exhausts the desktop
        // heap within a working day, and the symptom would be windows failing to draw rather than a crash here.
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail handle target");
        window.RequireForeground();
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
