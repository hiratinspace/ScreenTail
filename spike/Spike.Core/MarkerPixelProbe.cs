namespace ScreenTail.Spike.Core;

/// <summary>
/// Checks a captured region for the overlay's marker colour. The overlay paints itself mostly in the
/// marker colour, so a captured overlay scores high and an excluded one scores near zero.
/// </summary>
public static class MarkerPixelProbe
{
    public const double ExcludedBelow = 0.01;

    /// <summary>Fraction of BGRA pixels within <paramref name="tolerance"/> of the marker on every channel.</summary>
    public static double MatchFraction(ReadOnlySpan<byte> bgra, byte r, byte g, byte b, int tolerance = 24)
    {
        if (bgra.Length % 4 != 0)
        {
            throw new ArgumentException("Buffer length must be a multiple of 4 (BGRA).", nameof(bgra));
        }

        var pixels = bgra.Length / 4;
        if (pixels == 0)
        {
            return 0;
        }

        var hits = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (Math.Abs(bgra[i] - b) <= tolerance
                && Math.Abs(bgra[i + 1] - g) <= tolerance
                && Math.Abs(bgra[i + 2] - r) <= tolerance)
            {
                hits++;
            }
        }

        return (double)hits / pixels;
    }
}
