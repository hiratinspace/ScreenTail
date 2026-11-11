namespace ScreenTail.Core.Privacy;

/// <summary>The size a frame is given to the OCR engine at, and the enlargement asked for.</summary>
/// <param name="Width">The width of the picture the engine reads, in pixels.</param>
/// <param name="Height">Its height.</param>
/// <param name="Scale">The enlargement asked for, 1 when read as captured; the one applied on each axis is the size over the frame's.</param>
public readonly record struct OcrReadSize(uint Width, uint Height, double Scale);

/// <summary>
/// How large a frame is given to the OCR engine, and how a box the engine reports on that larger
/// picture is brought back to the frame's own pixels (ST-042). Kept apart from the Windows engine so
/// the arithmetic, which decides whether a glyph's edge is masked, is tested on every platform.
/// </summary>
public static class OcrScaling
{
    /// <summary>
    /// The most pixels the engine is given to read for one frame: a full HD frame at twice its size.
    /// What it costs grows with the pixels, so this bounds the time a frame of any size takes to what
    /// the full HD frame was measured to take.
    /// </summary>
    public const double PixelBudget = 4.0 * 1920 * 1080;

    /// <summary>
    /// The size a frame of <paramref name="width"/> by <paramref name="height"/> is read at: up to
    /// <paramref name="most"/> times larger, less where that would pass <see cref="PixelBudget"/> or
    /// <paramref name="maxSide"/>, never smaller than captured.
    /// </summary>
    public static OcrReadSize ReadSize(uint width, uint height, double most, uint maxSide)
    {
        var scale = Math.Min(most, Math.Sqrt(PixelBudget / Math.Max(1.0, (double)width * height)));
        scale = Math.Min(scale, maxSide / (double)Math.Max(1u, Math.Max(width, height)));
        if (scale <= 1.0)
        {
            return new OcrReadSize(width, height, 1.0);
        }

        // Down to whole pixels, so the picture is never past the budget or the largest side; and a frame
        // that would gain no pixel on either side is read as it is rather than resampled for nothing.
        var (readWidth, readHeight) = (Math.Max(width, (uint)Math.Floor(width * scale)), Math.Max(height, (uint)Math.Floor(height * scale)));
        return readWidth == width && readHeight == height
            ? new OcrReadSize(width, height, 1.0)
            : new OcrReadSize(readWidth, readHeight, scale);
    }

    /// <summary>
    /// A box the engine reported on the picture it read, in the frame's own pixels: divided on each axis
    /// by the enlargement applied on that axis (the read size over the frame's, which rounding to whole
    /// pixels makes differ from the one asked for), outward to whole pixels on all four sides, and held
    /// to the frame.
    /// </summary>
    public static OcrWord BoxBack(
        string text, double x, double y, double boxWidth, double boxHeight,
        uint width, uint height, OcrReadSize read)
    {
        var (byX, byY) = ((double)width / read.Width, (double)height / read.Height);
        var left = Math.Clamp(Math.Floor(x * byX), 0, width);
        var top = Math.Clamp(Math.Floor(y * byY), 0, height);
        var right = Math.Clamp(Math.Ceiling((x + boxWidth) * byX), left, width);
        var bottom = Math.Clamp(Math.Ceiling((y + boxHeight) * byY), top, height);
        return new OcrWord(text, (int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}
