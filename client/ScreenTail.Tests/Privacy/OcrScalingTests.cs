using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The size a frame is read at and how a box comes back from it (ST-042, review of PR #278). The engine
/// is given the frame enlarged to whole pixels, so the enlargement applied on each axis is the read
/// size over the frame's, not the one asked for; a box divided by the one asked for can come back a
/// pixel short of the glyph's edge, and nothing pads a mask after it.
/// </summary>
public sealed class OcrScalingTests
{
    private const uint MaxSide = 10000;

    [Theory]
    [InlineData(1920u, 1080u, 3840u, 2160u)]
    [InlineData(1080u, 1920u, 2160u, 3840u)]
    [InlineData(2560u, 1440u, 3840u, 2160u)]
    [InlineData(1u, 1u, 2u, 2u)]
    [InlineData(16u, 9u, 32u, 18u)]
    public void AFrameWithinTheBudgetIsReadAtTwiceItsSize(uint width, uint height, uint readWidth, uint readHeight)
    {
        var read = OcrScaling.ReadSize(width, height, 2.0, MaxSide);

        Assert.Equal((readWidth, readHeight), (read.Width, read.Height));
    }

    [Theory]
    [InlineData(3840u, 2160u)]
    [InlineData(3839u, 2160u)]
    [InlineData(3841u, 2160u)]
    [InlineData(7680u, 4320u)]
    [InlineData(12000u, 1000u)]
    public void AFrameAtOrPastTheBudgetIsReadAsCaptured(uint width, uint height)
    {
        var read = OcrScaling.ReadSize(width, height, 2.0, MaxSide);

        Assert.Equal((width, height, 1.0), (read.Width, read.Height, read.Scale));
    }

    [Theory]
    [InlineData(2560u, 1600u)]
    [InlineData(3440u, 1440u)]
    [InlineData(5120u, 1440u)]
    [InlineData(3000u, 2000u)]
    [InlineData(2879u, 1619u)]
    [InlineData(3600u, 2160u)]
    [InlineData(5760u, 1080u)]
    [InlineData(3840u, 1080u)]
    [InlineData(9000u, 500u)]
    public void AFrameNearTheBudgetIsReadLargerButNoLargerThanTheBudgetAllows(uint width, uint height)
    {
        var read = OcrScaling.ReadSize(width, height, 2.0, MaxSide);

        Assert.True(read.Width > width || read.Height > height, $"{width}x{height} was not enlarged.");
        Assert.True((double)read.Width * read.Height <= OcrScaling.PixelBudget, $"{read.Width}x{read.Height} is past the budget.");
        Assert.True(Math.Max(read.Width, read.Height) <= MaxSide);
        Assert.True(read.Width >= width && read.Height >= height);
    }

    [Theory]
    [InlineData(1920u, 1080u)]
    [InlineData(2560u, 1440u)]
    [InlineData(2560u, 1600u)]
    [InlineData(3440u, 1440u)]
    [InlineData(5120u, 1440u)]
    [InlineData(3840u, 2160u)]
    [InlineData(3839u, 2160u)]
    [InlineData(3000u, 2000u)]
    [InlineData(2879u, 1619u)]
    [InlineData(1080u, 1920u)]
    [InlineData(5760u, 1080u)]
    [InlineData(7u, 3u)]
    public void AGlyphAtTheFramesFarCornerComesBackWhole(uint width, uint height)
    {
        var read = OcrScaling.ReadSize(width, height, 2.0, MaxSide);
        var (x0, y0) = ((int)width - Math.Min(5, (int)width), (int)height - Math.Min(2, (int)height));

        Assert.True(Contains(Back(read, width, height, x0, y0, (int)width, (int)height), x0, y0, (int)width, (int)height));
    }

    [Fact]
    public void ABoxPastTheFrameIsClampedToIt()
    {
        var read = OcrScaling.ReadSize(1920, 1080, 2.0, MaxSide);

        var word = OcrScaling.BoxBack("x", -3, -3, 4000, 3000, 1920, 1080, read);

        Assert.Equal(new OcrWord("x", 0, 0, 1920, 1080), word);
    }

    /// <summary>
    /// For any frame and any glyph on it, the box brought back holds the glyph on all four sides. The
    /// engine is taken at its least: on the larger picture it reports only the whole pixels inside the
    /// glyph, dropping the ones the glyph's edge only partly covers.
    /// </summary>
    [Fact]
    public void ABoxBroughtBackAlwaysHoldsTheGlyph()
    {
        var random = new Random(278);
        var failures = 0;
        var example = string.Empty;
        for (var i = 0; i < 20000; i++)
        {
            var (width, height) = i % 4 == 0
                ? ((uint)random.Next(1, 64), (uint)random.Next(1, 64))
                : ((uint)random.Next(640, 8000), (uint)random.Next(400, 4500));
            var read = OcrScaling.ReadSize(width, height, random.Next(2) == 0 ? 2.0 : 1.0 + random.NextDouble(), MaxSide);
            var x0 = random.Next(0, (int)width);
            var y0 = random.Next(0, (int)height);
            var x1 = random.Next(x0 + 1, (int)width + 1);
            var y1 = random.Next(y0 + 1, (int)height + 1);
            if (!Contains(Back(read, width, height, x0, y0, x1, y1), x0, y0, x1, y1))
            {
                failures++;
                example = example.Length > 0 ? example : $"{width}x{height} read at {read.Width}x{read.Height}, glyph {x0},{y0} to {x1},{y1}";
            }
        }

        Assert.True(failures == 0, $"{failures} of 20000 boxes came back short, first {example}.");
    }

    /// <summary>The glyph's box on the picture the engine read, at its least, brought back.</summary>
    private static OcrWord Back(OcrReadSize read, uint width, uint height, int x0, int y0, int x1, int y1)
    {
        var (rx, ry) = ((double)read.Width / width, (double)read.Height / height);
        var (left, top) = (Math.Ceiling(x0 * rx), Math.Ceiling(y0 * ry));
        var (right, bottom) = (Math.Max(left, Math.Floor(x1 * rx)), Math.Max(top, Math.Floor(y1 * ry)));
        return OcrScaling.BoxBack("glyph", left, top, right - left, bottom - top, width, height, read);
    }

    private static bool Contains(OcrWord word, int x0, int y0, int x1, int y1) =>
        word.X <= x0 && word.Y <= y0 && word.X + word.Width >= x1 && word.Y + word.Height >= y1;
}
