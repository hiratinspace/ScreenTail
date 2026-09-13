namespace ScreenTail.Core.Capture;

/// <param name="Image">JPEG bytes, already downscaled. Never leaves the service unredacted (INV-1).</param>
/// <param name="Width">Stored size, after downscaling.</param>
/// <param name="SourceWidth">Size actually captured, before downscaling. What OCR would run against.</param>
/// <param name="Timing">Where the time went. The budget is per stage, so a total alone can't be acted on.</param>
public sealed record CapturedFrame(
    byte[] Image,
    int Width,
    int Height,
    int SourceWidth,
    int SourceHeight,
    CaptureTiming Timing);

/// <summary>
/// How long each stage took. ADR-0001 measured 178 ms p95 for the whole path against a 120 ms budget but
/// recorded only the total, which is not enough to know what to fix: a slow grab points at
/// Windows.Graphics.Capture, a slow encode points at the encoder.
/// </summary>
/// <param name="Grab">The BitBlt itself: pixels off the screen into a GDI bitmap.</param>
/// <param name="Convert">Turning that GDI bitmap into something the encoder can read. A copy, and not a free one.</param>
/// <param name="Resize">Downscaling to the stored size.</param>
/// <param name="Encode">JPEG encoding.</param>
public readonly record struct CaptureTiming(TimeSpan Grab, TimeSpan Convert, TimeSpan Resize, TimeSpan Encode)
{
    public TimeSpan Total => Grab + Convert + Resize + Encode;

    public override string ToString() =>
        $"grab {Grab.TotalMilliseconds:F1} ms, convert {Convert.TotalMilliseconds:F1} ms, resize {Resize.TotalMilliseconds:F1} ms, encode {Encode.TotalMilliseconds:F1} ms, total {Total.TotalMilliseconds:F1} ms";
}

/// <summary>Takes the picture (ST-025). The caller decides whether it is allowed to (ST-023) and stores it.</summary>
public interface IScreenshotCapturer
{
    /// <summary>
    /// Captures the window in front, or null when there is nothing to capture — the window closed between
    /// the click and the capture, or Windows refused. A null is a missing frame, never a blank one.
    /// </summary>
    /// <param name="expected">
    /// The window the caller's scope decision was about. When it no longer has the foreground, nothing is
    /// captured (INV-5).
    ///
    /// Without this the two are coupled only by hope: scope is decided on one task when the foreground
    /// changes, while the click loop drains every 50 ms and the scene sampler ticks every second, both
    /// reading a decision that may already be stale. A technician who clicks in ScreenConnect and
    /// immediately alt-tabs to Outlook would otherwise have their inbox photographed against a scope
    /// decision made about a different window. Zero means "whatever is in front", for callers with no
    /// decision to honour.
    /// </param>
    CapturedFrame? CaptureForegroundWindow(int maxEdge = Downscale.MaxEdge, nint expected = 0);

    /// <summary>
    /// Reduces the window in front to the small grid the scene sampler compares (ST-026), or null when
    /// there is nothing to look at.
    ///
    /// Separate from <see cref="CaptureForegroundWindow"/> because this runs once a second whether or not
    /// anything happened, and the answer is almost always "nothing did". Encoding a JPEG to find that out
    /// would spend the session's CPU budget on frames that are thrown away.
    /// </summary>
    byte[]? CaptureSceneGrid(nint expected = 0);
}
