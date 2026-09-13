using ScreenTail.Core.Capture;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// Why the grid is 17×16 and the threshold is 5 (ST-026).
///
/// These are the measurements that chose them, kept as assertions. The whole feature rests on one number
/// separating "a dialog appeared" from "a caret blinked", and that number is not obvious: the textbook
/// dHash grid of 9×8 cannot see a dialog on a 1080p screen at all, which would have shipped a sampler that
/// looked correct, passed its unit tests against synthetic hashes, and noticed nothing on a real desktop.
///
/// The screens here are drawn arithmetically rather than rendered, so they run on the Mac and mean the same
/// thing on every machine. A box filter is a box filter whether the pixels came from GDI or from a loop.
/// </summary>
public sealed class SceneGridTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>Things a still screen does by itself. None of these is a new thing to photograph.</summary>
    public static TheoryData<string, int, int, int, int> Quiet => new()
    {
        { "a blinking caret", 300, 200, 2, 18 },
        { "a taskbar clock", 1800, 1045, 60, 20 },
        { "a notification badge", 1700, 1050, 16, 16 },
    };

    /// <summary>Things worth a screenshot. The smallest is the one that decides the grid size.</summary>
    public static TheoryData<string, int, int> Dialogs => new()
    {
        { "a small error dialog", 300, 160 },
        { "a standard dialog", 400, 300 },
        { "a settings window", 520, 360 },
        { "a large window", 700, 420 },
    };

    [Theory]
    [MemberData(nameof(Quiet))]
    public void AScreenLookingAfterItselfStaysUnderTheThreshold(string what, int x, int y, int w, int h)
    {
        var threshold = new SceneSamplerOptions().Threshold;
        var before = Hash(Desktop());
        var after = Hash(Paint(Desktop(), x, y, w, h));

        var distance = before.DistanceTo(after);

        Assert.True(
            distance < threshold,
            $"{what} moved the fingerprint by {distance} of {SceneHash.Bits} bits, at or over the threshold of "
            + $"{threshold}; a screen doing this once a second would be photographed all day");
    }

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void ADialogAppearingClearsTheThreshold(string what, int w, int h)
    {
        var threshold = new SceneSamplerOptions().Threshold;
        var before = Hash(Desktop());
        var after = Hash(Paint(Desktop(), (Width - w) / 2, (Height - h) / 2, w, h));

        var distance = before.DistanceTo(after);

        Assert.True(
            distance >= threshold,
            $"{what} ({w}x{h}, {w * h * 100.0 / (Width * Height):F1}% of the screen) moved the fingerprint by "
            + $"only {distance} of {SceneHash.Bits} bits, under the threshold of {threshold}; it would never "
            + "be captured");
    }

    [Fact]
    public void HeavyPixelNoiseIsStillQuieterThanTheSmallestDialog()
    {
        // The margin the threshold sits in. If a change to the grid or the filter ever closes this gap,
        // there is no threshold that both notices dialogs and ignores a screen being itself.
        var before = Hash(Desktop());

        var noisy = Desktop();
        for (var i = 0; i < 2_000; i++)
        {
            noisy[i * 977 % noisy.Length] ^= 0xFF;
        }

        var noise = before.DistanceTo(Hash(noisy));
        var smallest = before.DistanceTo(Hash(Paint(Desktop(), (Width - 300) / 2, (Height - 160) / 2, 300, 160)));

        Assert.True(
            noise < smallest,
            $"0.1% of pixels inverted scored {noise} and the smallest dialog scored {smallest}; nothing separates them");
    }

    [Fact]
    public void TheDownwardComparisonIsWhatMakesADialogVisible()
    {
        // Why the hash walks both directions. dHash is blind along whichever axis it does not walk, and a
        // 17x16 grid of a 1080p screen is coarse enough that a dialog often changes a column's ordering
        // without changing a row's. Across only, the smallest dialog scored 7 against noise at 3 — a gap of
        // four. With the downward comparison it scores 19 against the same 3.
        var threshold = new SceneSamplerOptions().Threshold;
        var smallest = Hash(Desktop()).DistanceTo(Hash(Paint(Desktop(), (Width - 300) / 2, (Height - 160) / 2, 300, 160)));

        Assert.True(
            smallest >= threshold * 1.5,
            $"the smallest dialog scored {smallest} against a threshold of {threshold}; the margin is too thin "
            + "to survive a real screen");
    }

    [Fact]
    public void ARegionChangingShadeWithoutChangingOrderIsNotSeen()
    {
        // A known and deliberate limit, recorded so nobody assumes otherwise. Any hash built on orderings is
        // blind to a region that changes brightness while staying on the same side of everything around it:
        // a dark log pane going from grey to darker grey. That is the price of ignoring a screen dimming and
        // a caret blinking, and clicks still photograph what the technician actually does.
        var before = Hash(Paint2(Desktop(), 760, 200, 900, 400, shade: 120));
        var after = Hash(Paint2(Desktop(), 760, 200, 900, 400, shade: 60));

        Assert.Equal(0, before.DistanceTo(after));
    }

    [Fact]
    public void TheWholeScreenGoingDarkIsNotAChange()
    {
        // A theme switching, a monitor dimming, night light coming on. The hash compares neighbours, so a
        // change that keeps every cell in the same order relative to the next leaves it alone.
        var desktop = Desktop();
        var dimmed = desktop.Select(p => (byte)(p / 2)).ToArray();

        var distance = Hash(desktop).DistanceTo(Hash(dimmed));

        Assert.True(distance < new SceneSamplerOptions().Threshold, $"dimming the screen scored {distance}");
    }

    [Fact]
    public void EveryCellIsMadeOfTheWholeScreen()
    {
        // A box filter, not point sampling. Point sampling 1080p down to 272 cells would read 272 pixels and
        // miss anything that did not land on one — which is most of what appears on a screen.
        var screen = new byte[Width * Height];
        var cellWidth = Width / PerceptualHash.Columns;
        var cellHeight = Height / PerceptualHash.Rows;

        // One bright block filling a single cell, offset so no obvious sampling point sits inside it.
        for (var y = cellHeight + 7; y < (cellHeight * 2) - 7; y++)
        {
            for (var x = cellWidth + 7; x < (cellWidth * 2) - 7; x++)
            {
                screen[(y * Width) + x] = 255;
            }
        }

        var grid = SceneGrid.From(screen, Width, Height);

        Assert.True(grid.Any(cell => cell > 0), "a block the size of a cell did not reach the grid at all");
        Assert.All(grid, cell => Assert.True(cell < 255, "a cell took one pixel's value instead of an average"));
    }

    [Fact]
    public void AScreenSmallerThanTheGridIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneGrid.From(new byte[16], 4, 4));
    }

    private static SceneHash Hash(byte[] screen) => PerceptualHash.OfGrid(SceneGrid.From(screen, Width, Height));

    /// <summary>A flat region of one shade, for the case the hash deliberately cannot see.</summary>
    private static byte[] Paint2(byte[] screen, int x0, int y0, int width, int height, byte shade)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                screen[(y * Width) + x] = shade;
            }
        }

        return screen;
    }

    /// <summary>A plausible desktop: a light window, a title bar, rows of text, a taskbar.</summary>
    private static byte[] Desktop()
    {
        var screen = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
            {
                byte shade = 243;
                if (y > Height - 48)
                {
                    shade = 32;
                }
                else if (y < 40)
                {
                    shade = 210;
                }
                else if (y % 34 < 14 && x is > 100 and < 700)
                {
                    shade = 40;
                }

                screen[row + x] = shade;
            }
        }

        return screen;
    }

    /// <summary>Paints a window over the screen: a light panel with a dark title strip and a line of text.</summary>
    private static byte[] Paint(byte[] screen, int x0, int y0, int width, int height)
    {
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                var inTitle = y - y0 < 30;
                var inText = y - y0 is > 60 and < 78 && x - x0 > 20 && x - x0 < width - 40;
                screen[(y * Width) + x] = (byte)(inTitle ? 70 : inText ? 30 : 250);
            }
        }

        return screen;
    }
}
