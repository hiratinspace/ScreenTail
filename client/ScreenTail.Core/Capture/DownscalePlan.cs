namespace ScreenTail.Core.Capture;

/// <param name="Width">Target width in pixels.</param>
/// <param name="Scale">1.0 when the frame is already small enough and no resampling is needed.</param>
public readonly record struct DownscalePlan(int Width, int Height, double Scale)
{
    public bool Resamples => Scale < 1.0;
}

/// <summary>
/// How large a stored frame should be (ST-025).
///
/// The long edge is capped at 1600 pixels, which ADR-0001 measured as legible for the text a technician
/// needs to read back — service names, error codes, dialog titles. OCR runs on the full-size capture before
/// this (ADR-0001 finding 2a), so shrinking costs nothing in recognition and saves the disk budget, the
/// upload, and most of the encode time.
/// </summary>
public static class Downscale
{
    public const int MaxEdge = 1600;

    public static DownscalePlan For(int width, int height, int maxEdge = MaxEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdge);

        var longest = Math.Max(width, height);
        if (longest <= maxEdge)
        {
            // Never upscale: a small dialog stays its own size rather than being blown up into a blur.
            return new DownscalePlan(width, height, 1.0);
        }

        var scale = (double)maxEdge / longest;

        // Round rather than truncate, and never to zero: a 4000×1 window is absurd but must not become 0 px.
        return new DownscalePlan(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)),
            scale);
    }
}
