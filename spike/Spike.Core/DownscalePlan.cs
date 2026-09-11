namespace ScreenTail.Spike.Core;

public static class DownscalePlan
{
    public const int DefaultMaxLongEdge = 1600;

    /// <summary>Fits a frame inside the long-edge limit, preserving aspect ratio. Never upscales.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxLongEdge = DefaultMaxLongEdge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLongEdge, 1);

        var longEdge = Math.Max(width, height);
        if (longEdge <= maxLongEdge)
        {
            return (width, height);
        }

        var scale = (double)maxLongEdge / longEdge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Approximate on-frame em height in pixels of text of the given point size, after display scaling
    /// and a downscale factor (output width / input width).
    /// </summary>
    public static double TextPixelHeight(double points, double displayScale, double downscaleFactor) =>
        points * 96.0 / 72.0 * displayScale * downscaleFactor;
}
