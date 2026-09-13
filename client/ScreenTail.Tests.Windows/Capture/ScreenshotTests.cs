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

        var frame = new ScreenshotCapturer().CaptureForegroundWindow();

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

        var frame = new ScreenshotCapturer().CaptureForegroundWindow();

        Assert.NotNull(frame);
        Assert.True(frame.Image.Length > 2_000, $"the frame encoded to {frame.Image.Length} bytes, which is what a blank capture looks like");
    }

    [Fact]
    public void WhatEachStageOfCaptureCosts()
    {
        // The measurement this ticket is really for.
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var window = DesktopWindow.Create("ScreenTail timing target");
        Assert.SkipUnless(window.TakeForeground(), "Could not take the foreground.");
        var capturer = new ScreenshotCapturer();

        // One to warm the JIT and the codec, then a run whose worst case is the honest figure.
        capturer.CaptureForegroundWindow();
        var frames = Enumerable.Range(0, 10).Select(_ => capturer.CaptureForegroundWindow()).Where(f => f is not null).ToList();
        Assert.NotEmpty(frames);

        var worst = frames.MaxBy(f => f!.Timing.Total)!;
        var megapixels = worst.SourceWidth * (double)worst.SourceHeight / 1_000_000;
        var perMegapixel = worst.Timing.Total.TotalMilliseconds / megapixels;

        Measurements.Record(
            $"Capture {worst.SourceWidth}x{worst.SourceHeight} → {worst.Width}x{worst.Height}, {worst.Image.Length / 1024} KB: **{worst.Timing}**");
        Measurements.Record(
            $"Capture cost **{perMegapixel:F0} ms/megapixel** — a 4K frame (8.3 MP) would be about **{perMegapixel * 8.3:F0} ms** against a 120 ms budget");

        // The budget is for 4K, which this display is not, so it is not asserted here — extrapolating a
        // pass would be worse than reporting the number. What is asserted is that nothing has become
        // pathological since ADR-0001 measured this path.
        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");
        Assert.True(
            worst.Timing.Total.TotalMilliseconds < 500,
            $"capture took {worst.Timing.Total.TotalMilliseconds:F0} ms for {megapixels:F1} MP ({worst.Timing})");
    }

    [Fact]
    public void ASmallWindowIsNotUpscaled()
    {
        Assert.SkipUnless(CanCapture, "This machine cannot take screenshots.");
        using var window = DesktopWindow.Create("ScreenTail small target");
        Assert.SkipUnless(window.TakeForeground(), "Could not take the foreground.");

        var frame = new ScreenshotCapturer().CaptureForegroundWindow();

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
        var capturer = new ScreenshotCapturer();

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
