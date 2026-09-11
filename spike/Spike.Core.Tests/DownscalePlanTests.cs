namespace ScreenTail.Spike.Core.Tests;

public class DownscalePlanTests
{
    [Theory]
    [InlineData(3840, 2160, 1600, 900)]
    [InlineData(1920, 1080, 1600, 900)]
    [InlineData(2160, 3840, 900, 1600)]
    [InlineData(2560, 1600, 1600, 1000)]
    [InlineData(1600, 1600, 1600, 1600)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(5000, 1, 1600, 1)]
    public void Fit_KeepsAspectAndNeverUpscales(int w, int h, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), DownscalePlan.Fit(w, h));
    }

    [Fact]
    public void Fit_RejectsEmptyFrames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DownscalePlan.Fit(0, 100));
    }

    [Theory]
    [InlineData(1.0, 1.0, 12.0)]
    [InlineData(1.5, 1600.0 / 3840, 7.5)]
    [InlineData(1.0, 1600.0 / 3840, 5.0)]
    [InlineData(2.0, 1600.0 / 3840, 10.0)]
    public void TextPixelHeight_For9pt(double displayScale, double downscale, double expectedPx)
    {
        Assert.Equal(expectedPx, DownscalePlan.TextPixelHeight(9, displayScale, downscale), precision: 3);
    }
}
