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
    public void GdiObjectsDoNotGrowAcrossCapturesAndResizes()
    {
        // Every capture selects a bitmap into a DC, and a resize throws the bitmap away and makes another.
        // Leaking any of them exhausts the desktop heap within a working day, and the symptom would be
        // windows failing to draw rather than a crash here. GDI objects are not kernel handles, so the
        // process handle count this used to watch could not see one leak, and a window that never changed
        // size never took the path that frees the old bitmap (2026-10-12 review, T-15).
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail handle target");
        window.RequireForeground();
        using var capturer = new ScreenshotCapturer();

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        for (var i = 0; i < 6; i++)
        {
            CaptureResized(window, capturer, i);
        }

        self.Refresh();
        var gdiBefore = GetGuiResources(self.Handle, GR_GDIOBJECTS);
        var handlesBefore = self.HandleCount;
        for (var i = 0; i < 50; i++)
        {
            CaptureResized(window, capturer, i);
        }

        self.Refresh();
        var gdiGrowth = (long)GetGuiResources(self.Handle, GR_GDIOBJECTS) - gdiBefore;
        var handleGrowth = self.HandleCount - handlesBefore;
        Measurements.Record($"50 captures across 50 resizes grew GDI objects by **{gdiGrowth}** and kernel handles by {handleGrowth}");
        Assert.True(gdiGrowth < 10, $"GDI object count grew by {gdiGrowth} over 50 captures across resizes");
        Assert.True(handleGrowth < 50, $"handle count grew by {handleGrowth} over 50 captures across resizes");
    }

    /// <summary>One capture of each kind at a size that differs from the last call's.</summary>
    private static void CaptureResized(DesktopWindow window, ScreenshotCapturer capturer, int turn)
    {
        window.Resize(turn % 2 == 0 ? 420 : 520, turn % 2 == 0 ? 220 : 300);
        capturer.CaptureForegroundWindow();
        capturer.CaptureSceneGrid();
    }

    [Fact]
    public void TheSceneGridSeesWhatIsInTheWindow()
    {
        // ST-026 lives on this grid: a flat one would say the screen never changes and the sampler would
        // never take a frame, with nothing to say so (2026-10-12 review, T-11). The window is painted light
        // on the left and dark on the right, and the grid has to say the same.
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail scene target");
        window.RequireForeground();
        window.ShowContrast(ContrastLayout.SideBySide);
        Thread.Sleep(150);
        using var capturer = new ScreenshotCapturer();

        // The same scope rule as a full capture: the grid is of the window that was judged, or nothing.
        Assert.Null(capturer.CaptureSceneGrid(expected: window.Handle + 1));
        var grid = capturer.CaptureSceneGrid(expected: window.Handle);

        Assert.NotNull(grid);
        Assert.Equal(PerceptualHash.GridLength, grid.Length);
        var left = Average(grid, rows: (5, 15), columns: (1, 7));
        var right = Average(grid, rows: (5, 15), columns: (10, 16));
        Measurements.Record($"Scene grid of a window painted light left, dark right: left **{left:F0}**, right **{right:F0}** (0 black, 255 white)");
        Assert.True(left > 160, $"the light half of the window averaged {left:F0} in the grid");
        Assert.True(right < 96, $"the dark half of the window averaged {right:F0} in the grid");
    }

    [Fact]
    public void TheSceneGridChangesWhenTheWindowDoesAndNotOtherwise()
    {
        // The sampler's own question, asked of real pixels: two looks at a still window are the same scene,
        // and the window repainting is a change past the threshold it acts on.
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail scene change target");
        window.RequireForeground();
        window.ShowContrast(ContrastLayout.SideBySide);
        Thread.Sleep(150);
        using var capturer = new ScreenshotCapturer();

        var first = capturer.CaptureSceneGrid(expected: window.Handle);
        var again = capturer.CaptureSceneGrid(expected: window.Handle);
        window.ShowContrast(ContrastLayout.Stacked);
        Thread.Sleep(150);
        var repainted = capturer.CaptureSceneGrid(expected: window.Handle);

        Assert.NotNull(first);
        Assert.NotNull(again);
        Assert.NotNull(repainted);
        var threshold = new SceneSamplerOptions().Threshold;
        var still = PerceptualHash.OfGrid(first).DistanceTo(PerceptualHash.OfGrid(again));
        var changed = PerceptualHash.OfGrid(first).DistanceTo(PerceptualHash.OfGrid(repainted));
        var cellsChanged = first.Zip(repainted, (a, b) => Math.Abs(a - b) > 64).Count(c => c);
        Measurements.Record($"Scene hash distance: still window **{still}**, repainted window **{changed}** ({cellsChanged} of {first.Length} cells changed; threshold {threshold})");
        Assert.True(still < threshold, $"two looks at a still window were {still} bits apart, threshold {threshold}");
        Assert.True(changed >= threshold, $"a repainted window was only {changed} bits away, threshold {threshold}");
        Assert.True(cellsChanged >= first.Length / 6, $"only {cellsChanged} of {first.Length} cells changed when half the window was repainted");
    }

    [Fact]
    public void NothingIsCapturedWhileATopmostWindowCoversTheWindowInFront()
    {
        // The grab is of the screen over the window's rectangle, so a toast, a popup or any topmost window
        // lying over an in-scope window is stored with it (2026-10-12 review, S-4; the topmost half of
        // P-6). Until the grab is of the window itself, a sample with something on top is skipped.
        Hardware.RequireScreenCapture();
        using var window = DesktopWindow.Create("ScreenTail covered target");
        window.RequireForeground();
        using var capturer = new ScreenshotCapturer();
        Assert.NotNull(capturer.CaptureForegroundWindow(expected: window.Handle));

        var bounds = window.Bounds;
        using var toast = DesktopWindow.Create("ScreenTail toast");
        toast.ShowTopmostAt(bounds.Left + 40, bounds.Top + 40, 160, 80);
        Thread.Sleep(150);
        Assert.True(window.IsForeground || window.TakeForeground(TimeSpan.FromSeconds(2)), "the topmost window took the foreground, so a null here would prove nothing about covering");

        Assert.Null(capturer.CaptureForegroundWindow(expected: window.Handle));
        Assert.Equal(1, capturer.SkippedCovered);

        // Beside the window rather than over it, the same topmost window is no reason to skip.
        toast.MoveTo(bounds.Right + 20, bounds.Top, 160, 80);
        Thread.Sleep(150);
        Assert.NotNull(capturer.CaptureForegroundWindow(expected: window.Handle));
        Assert.Equal(1, capturer.SkippedCovered);
    }

    [Fact]
    public void AQueuedCaptureDoesNotPhotographTheWindowThatHasSinceLeftTheForeground()
    {
        // The capturer's lock is held for the whole of a grab, about 300 ms at 4K, and the foreground and
        // the rectangle used to be read before waiting for it. A capture queued behind a slow one then
        // photographed the old rectangle after an Alt+Tab, whatever was there by then (2026-10-12 review,
        // S-5). The seam stands in for the wait: the foreground moves while this capture holds the lock
        // and before it reads anything.
        Hardware.RequireScreenCapture();
        using var first = DesktopWindow.Create("ScreenTail first target");
        using var second = DesktopWindow.Create("ScreenTail second target");
        first.RequireForeground();
        using var capturer = new ScreenshotCapturer();
        var moved = false;
        capturer.BeforeGrab = () =>
        {
            if (!moved)
            {
                moved = second.TakeForeground(TimeSpan.FromSeconds(2));
            }
        };

        var frame = capturer.CaptureForegroundWindow(expected: first.Handle);

        Assert.True(moved, "the second window could not take the foreground, so nothing moved and a frame here proves nothing");
        Assert.Null(frame);

        // The control: with the judged window back in front, the same capture takes its picture.
        capturer.BeforeGrab = null;
        Assert.True(first.TakeForeground(TimeSpan.FromSeconds(2)));
        Assert.NotNull(capturer.CaptureForegroundWindow(expected: first.Handle));
    }

    /// <summary>Mean luma of the cells in a block of the grid; both ranges are end-exclusive.</summary>
    private static double Average(byte[] grid, (int From, int To) rows, (int From, int To) columns)
    {
        var total = 0.0;
        var count = 0;
        for (var row = rows.From; row < rows.To; row++)
        {
            for (var column = columns.From; column < columns.To; column++)
            {
                total += grid[(row * PerceptualHash.Columns) + column];
                count++;
            }
        }

        return total / count;
    }

    private const uint GR_GDIOBJECTS = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
