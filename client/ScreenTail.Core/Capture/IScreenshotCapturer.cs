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
/// <param name="Grab">Getting the pixels off the screen.</param>
/// <param name="Resize">Downscaling to the stored size.</param>
/// <param name="Encode">JPEG encoding.</param>
public readonly record struct CaptureTiming(TimeSpan Grab, TimeSpan Resize, TimeSpan Encode)
{
    public TimeSpan Total => Grab + Resize + Encode;

    public override string ToString() =>
        $"grab {Grab.TotalMilliseconds:F1} ms, resize {Resize.TotalMilliseconds:F1} ms, encode {Encode.TotalMilliseconds:F1} ms, total {Total.TotalMilliseconds:F1} ms";
}

/// <summary>Takes the picture (ST-025). The caller decides whether it is allowed to (ST-023) and stores it.</summary>
public interface IScreenshotCapturer
{
    /// <summary>
    /// Captures the window in front, or null when there is nothing to capture — the window closed between
    /// the click and the capture, or Windows refused. A null is a missing frame, never a blank one.
    /// </summary>
    CapturedFrame? CaptureForegroundWindow(int maxEdge = Downscale.MaxEdge);
}
