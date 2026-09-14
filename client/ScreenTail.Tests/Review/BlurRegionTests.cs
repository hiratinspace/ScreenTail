using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using Rect = ScreenTail.Core.Review.Rect;
using Size = ScreenTail.Core.Review.Size;

namespace ScreenTail.Tests.Review;

/// <summary>
/// ST-075: the rectangle a technician drags becomes the rectangle that gets destroyed. Getting this wrong
/// blurs the wrong part of a picture, records that the right part was covered, and loses the original.
/// </summary>
public sealed class BlurRegionTests
{
    [Fact]
    public void ARectangleOnAHalfSizePreviewIsTwiceThatInTheFrame()
    {
        var region = BlurRegion.From(new Rect(10, 20, 30, 40), new Size(800, 450), new Size(1600, 900));

        Assert.Equal(20, region.X);
        Assert.Equal(40, region.Y);
        Assert.Equal(60, region.Width);
        Assert.Equal(80, region.Height);
        Assert.Equal(MaskKind.UserBlur, region.Kind);
    }

    [Fact]
    public void EachAxisIsScaledFromItsOwnDimension()
    {
        // Uniform scaling makes one factor look sufficient - right until the pane is a different aspect,
        // when every blur lands slightly wrong and nobody notices, because the picture underneath is gone.
        var region = BlurRegion.From(new Rect(100, 100, 100, 100), new Size(1000, 250), new Size(2000, 1000));

        Assert.Equal(200, region.X);
        Assert.Equal(400, region.Y);
        Assert.Equal(200, region.Width);
        Assert.Equal(400, region.Height);
    }

    [Fact]
    public void APartialPixelIsRoundedOutwardsNotInwards()
    {
        // A blur one pixel short of what was asked for leaves the top row of a password on the screen.
        var region = BlurRegion.From(new Rect(10.4, 10.4, 5.2, 5.2), new Size(100, 100), new Size(100, 100));

        Assert.Equal(10, region.X);
        Assert.Equal(10, region.Y);
        Assert.Equal(6, region.Width);
        Assert.Equal(6, region.Height);
    }

    [Fact]
    public void ADragOffTheEdgeStopsAtTheEdge()
    {
        // Dragging past the image is how people select to the end of something. The schema's minimum is 0
        // and the frame's own width is the maximum, so a region that runs off it is not storable.
        var region = BlurRegion.From(new Rect(-50, -50, 2000, 2000), new Size(800, 450), new Size(1600, 900));

        Assert.Equal(0, region.X);
        Assert.Equal(0, region.Y);
        Assert.Equal(1600, region.Width);
        Assert.Equal(900, region.Height);
    }

    [Fact]
    public void ADragUpAndLeftIsTheSameRectangleAsOneDownAndRight()
    {
        var downRight = BlurRegion.From(Rect.Between(10, 10, 40, 50), new Size(100, 100), new Size(100, 100));
        var upLeft = BlurRegion.From(Rect.Between(40, 50, 10, 10), new Size(100, 100), new Size(100, 100));

        Assert.Equal(downRight, upLeft);
    }

    [Fact]
    public void ARegionIsNeverZeroSized()
    {
        // masked_regions has minimum 1 on width and height, so a hairline drag must still be a rectangle
        // the store will accept rather than one that fails validation after the image has been replaced.
        var region = BlurRegion.From(new Rect(10, 10, 0, 0), new Size(100, 100), new Size(100, 100));

        Assert.Equal(1, region.Width);
        Assert.Equal(1, region.Height);
    }

    [Fact]
    public void AClickThatWobbledIsNotABlur()
    {
        // Otherwise clicking an enlarged frame destroys four pixels of it, permanently, with no undo the
        // technician knew they needed.
        Assert.False(BlurRegion.IsMeaningful(new Rect(10, 10, 2, 2)));
        Assert.True(BlurRegion.IsMeaningful(new Rect(10, 10, 40, 12)));
    }

    [Fact]
    public void AnImageThatWasNeverDrawnIsRefused()
    {
        // Zero would divide, and the result would be a region of NaN written over a real frame.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BlurRegion.From(new Rect(0, 0, 10, 10), new Size(0, 0), new Size(1600, 900)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BlurRegion.From(new Rect(0, 0, 10, 10), new Size(800, 450), new Size(0, 0)));
    }
}
