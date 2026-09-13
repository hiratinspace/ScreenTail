using ScreenTail.Core.Capture;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// Screens drawn arithmetically, for ST-026's tests.
///
/// The sampler's whole job is to tell a screen that changed from one that only looks like it did, and that
/// judgement is about real proportions: how much of a 1920×1080 desktop a dialog covers, how little of it a
/// caret does. Tests built on random grids measure the hash's arithmetic instead, and they agree with
/// whatever the grid size happens to be — which is how a sampler that cannot see a dialog would pass them.
///
/// Drawn rather than rendered so it runs on the Mac and means the same thing on every machine. A box filter
/// is a box filter whether the pixels came from GDI or from a loop.
/// </summary>
internal sealed class SyntheticScreen
{
    public const int Width = 1920;
    public const int Height = 1080;

    private readonly byte[] _pixels;
    private readonly int _width;
    private readonly int _height;
    private readonly int _scale;

    private SyntheticScreen(byte[] pixels, int width, int height, int scale) =>
        (_pixels, _width, _height, _scale) = (pixels, width, height, scale);

    /// <summary>
    /// A plausible desktop: a light window, a title bar, rows of text, a taskbar.
    /// </summary>
    /// <param name="scale">
    /// Divides the resolution, leaving every proportion alone. The grid is 17×16 either way, so a screen at
    /// a quarter the size produces the same cells from the same relative areas — and the sequences that
    /// play twenty simulated minutes do sixteen times less work for it. Full size is for the tests that
    /// measure sensitivity, where the real pixel counts are the point.
    /// </param>
    public static SyntheticScreen Desktop(int scale = 1)
    {
        var width = Width / scale;
        var height = Height / scale;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                byte shade = 243;
                if (y > height - (48 / scale))
                {
                    shade = 32;
                }
                else if (y < 40 / scale)
                {
                    shade = 210;
                }
                else if (y % Math.Max(34 / scale, 2) < Math.Max(14 / scale, 1) && x > 100 / scale && x < 700 / scale)
                {
                    shade = 40;
                }

                pixels[row + x] = shade;
            }
        }

        return new SyntheticScreen(pixels, width, height, scale);
    }

    /// <summary>Paints a window: a light panel with a dark title strip and a line of text.</summary>
    public SyntheticScreen Window(int x0, int y0, int width, int height)
    {
        var (left, top, w, h) = (x0 / _scale, y0 / _scale, width / _scale, height / _scale);
        for (var y = top; y < Math.Min(top + h, _height); y++)
        {
            for (var x = left; x < Math.Min(left + w, _width); x++)
            {
                var inTitle = y - top < Math.Max(30 / _scale, 1);
                var inText = y - top > 60 / _scale && y - top < 78 / _scale
                    && x - left > 20 / _scale && x - left < w - (40 / _scale);
                _pixels[(y * _width) + x] = (byte)(inTitle ? 70 : inText ? 30 : 250);
            }
        }

        return this;
    }

    /// <summary>A solid block: a caret, a clock, a badge.</summary>
    public SyntheticScreen Block(int x0, int y0, int width, int height, byte shade)
    {
        var (left, top, w, h) = (x0 / _scale, y0 / _scale, Math.Max(width / _scale, 1), Math.Max(height / _scale, 1));
        for (var y = top; y < Math.Min(top + h, _height); y++)
        {
            for (var x = left; x < Math.Min(left + w, _width); x++)
            {
                _pixels[(y * _width) + x] = shade;
            }
        }

        return this;
    }

    public SceneHash Hash() => PerceptualHash.OfGrid(SceneGrid.From(_pixels, _width, _height));
}
