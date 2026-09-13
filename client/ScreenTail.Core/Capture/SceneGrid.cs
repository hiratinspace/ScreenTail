namespace ScreenTail.Core.Capture;

/// <summary>
/// Reduces a screen to the small grid <see cref="PerceptualHash"/> works on (ST-026).
///
/// A box filter — every source pixel contributes to exactly one cell — rather than point sampling. Point
/// sampling a 1920×1080 screen down to a handful of cells would read a few dozen pixels and miss anything
/// that did not happen to land on one of them, which for this purpose means missing most dialogs. The same
/// averaging is what <c>StretchBlt</c> with <c>HALFTONE</c> does on Windows, so the grid the service
/// produces from the screen and the grid a test produces from a synthetic one mean the same thing.
/// </summary>
public static class SceneGrid
{
    /// <param name="luminance">One byte of brightness per pixel, row-major.</param>
    public static byte[] From(ReadOnlySpan<byte> luminance, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, PerceptualHash.Columns);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, PerceptualHash.Rows);
        if (luminance.Length < width * height)
        {
            throw new ArgumentException($"A {width}x{height} screen needs {width * height} bytes.", nameof(luminance));
        }

        var grid = new byte[PerceptualHash.GridLength];
        for (var row = 0; row < PerceptualHash.Rows; row++)
        {
            var top = (int)((long)row * height / PerceptualHash.Rows);
            var bottom = (int)((long)(row + 1) * height / PerceptualHash.Rows);
            for (var column = 0; column < PerceptualHash.Columns; column++)
            {
                var left = (int)((long)column * width / PerceptualHash.Columns);
                var right = (int)((long)(column + 1) * width / PerceptualHash.Columns);

                long total = 0;
                for (var y = top; y < bottom; y++)
                {
                    var start = y * width;
                    for (var x = left; x < right; x++)
                    {
                        total += luminance[start + x];
                    }
                }

                var pixels = (long)(bottom - top) * (right - left);
                grid[(row * PerceptualHash.Columns) + column] = (byte)(pixels == 0 ? 0 : total / pixels);
            }
        }

        return grid;
    }
}
