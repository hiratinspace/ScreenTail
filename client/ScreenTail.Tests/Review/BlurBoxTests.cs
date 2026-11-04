using ScreenTail.Core.Review;
using Rect = ScreenTail.Core.Review.Rect;
using Size = ScreenTail.Core.Review.Size;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The blur box a keyboard places over an enlarged screenshot (ST-084 follow-up). It is held in whole
/// percent of the picture, so every arrow moves it by the same share of the screenshot whatever size the
/// pane is, and what a screen reader hears is exactly where it is. Its rectangle goes to the same
/// <see cref="BlurRegion.From"/> a mouse drag goes to.
/// </summary>
public sealed class BlurBoxTests
{
    [Fact]
    public void ItStartsAFifthOfThePictureInTheMiddle()
    {
        var box = BlurBox.Centred;

        Assert.Equal((40, 40, 20, 20), (box.X, box.Y, box.Width, box.Height));
        Assert.Equal(BlurBox.DefaultSide, box.Width);
        Assert.Equal(100, box.X + box.Width + box.X);
    }

    [Theory]
    [InlineData(BlurBoxDirection.Right, 41, 40)]
    [InlineData(BlurBoxDirection.Left, 39, 40)]
    [InlineData(BlurBoxDirection.Down, 40, 41)]
    [InlineData(BlurBoxDirection.Up, 40, 39)]
    public void AnArrowMovesItOnePercent(BlurBoxDirection direction, int x, int y)
    {
        var box = BlurBox.Centred.Nudge(direction, resize: false, large: false);

        Assert.Equal((x, y, 20, 20), (box.X, box.Y, box.Width, box.Height));
    }

    [Theory]
    [InlineData(BlurBoxDirection.Right, 50, 40)]
    [InlineData(BlurBoxDirection.Left, 30, 40)]
    [InlineData(BlurBoxDirection.Down, 40, 50)]
    [InlineData(BlurBoxDirection.Up, 40, 30)]
    public void CtrlMovesItTenPercent(BlurBoxDirection direction, int x, int y)
    {
        var box = BlurBox.Centred.Nudge(direction, resize: false, large: true);

        Assert.Equal((x, y, 20, 20), (box.X, box.Y, box.Width, box.Height));
        Assert.Equal(10, BlurBox.LargeStep);
        Assert.Equal(1, BlurBox.Step);
    }

    [Theory]
    [InlineData(BlurBoxDirection.Right, false, 21, 20)]
    [InlineData(BlurBoxDirection.Left, false, 19, 20)]
    [InlineData(BlurBoxDirection.Down, false, 20, 21)]
    [InlineData(BlurBoxDirection.Up, false, 20, 19)]
    [InlineData(BlurBoxDirection.Right, true, 30, 20)]
    [InlineData(BlurBoxDirection.Up, true, 20, 10)]
    public void ShiftResizesItFromItsTopLeftCorner(BlurBoxDirection direction, bool large, int width, int height)
    {
        var box = BlurBox.Centred.Nudge(direction, resize: true, large);

        Assert.Equal((40, 40, width, height), (box.X, box.Y, box.Width, box.Height));
    }

    [Fact]
    public void ItCannotBeMovedOffThePicture()
    {
        var box = BlurBox.Centred;
        for (var i = 0; i < 20; i++)
        {
            box = box.Nudge(BlurBoxDirection.Right, resize: false, large: true).Nudge(BlurBoxDirection.Down, resize: false, large: true);
        }

        Assert.Equal((80, 80, 20, 20), (box.X, box.Y, box.Width, box.Height));

        for (var i = 0; i < 20; i++)
        {
            box = box.Nudge(BlurBoxDirection.Left, resize: false, large: true).Nudge(BlurBoxDirection.Up, resize: false, large: false);
        }

        Assert.Equal((0, 60, 20, 20), (box.X, box.Y, box.Width, box.Height));
        box = box.Move(-1_000, -1_000);
        Assert.Equal((0, 0), (box.X, box.Y));
    }

    [Fact]
    public void ItCannotGrowPastThePictureAndAtTheEdgeItGrowsTheOtherWay()
    {
        // Moved to the right edge, then widened: the right side cannot go further, so the box grows to
        // the left. Covering everything up to the edge of a screenshot is how a sidebar gets blurred.
        var box = BlurBox.Centred.Move(40, 0);
        Assert.Equal(80, box.X);

        box = box.Nudge(BlurBoxDirection.Right, resize: true, large: true);
        Assert.Equal((70, 30), (box.X, box.Width));

        for (var i = 0; i < 20; i++)
        {
            box = box.Nudge(BlurBoxDirection.Right, resize: true, large: true).Nudge(BlurBoxDirection.Down, resize: true, large: true);
        }

        Assert.Equal((0, 0, 100, 100), (box.X, box.Y, box.Width, box.Height));
    }

    [Fact]
    public void ItCannotShrinkBelowTheMinimum()
    {
        var box = BlurBox.Centred;
        for (var i = 0; i < 30; i++)
        {
            box = box.Nudge(BlurBoxDirection.Left, resize: true, large: false).Nudge(BlurBoxDirection.Up, resize: true, large: true);
        }

        Assert.Equal((40, 40, BlurBox.MinimumSide, BlurBox.MinimumSide), (box.X, box.Y, box.Width, box.Height));
        Assert.Equal(2, BlurBox.MinimumSide);
    }

    [Fact]
    public void AnyNumbersMakeABoxInsideThePicture()
    {
        Assert.Equal((0, 0, 100, 100), Parts(BlurBox.Of(-5, -5, 500, 500)));
        Assert.Equal((98, 98, 2, 2), Parts(BlurBox.Of(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue)));
        Assert.Equal((0, 0, 2, 2), Parts(BlurBox.Of(int.MinValue, int.MinValue, 0, 0)));
        Assert.Equal((0, 0, 100, 100), Parts(BlurBox.Of(0, 0, int.MaxValue, int.MaxValue).Resize(int.MaxValue, int.MaxValue)));
        Assert.Equal((80, 0, 2, 2), Parts(BlurBox.Centred.Move(int.MaxValue, int.MinValue).Resize(int.MinValue, int.MinValue)));
    }

    [Fact]
    public void ItsRectangleIsItsShareOfThePictureAsDrawn()
    {
        var drawn = BlurBox.Of(25, 10, 50, 20).Drawn(new Size(400, 300));

        Assert.Equal(new Rect(100, 30, 200, 60), drawn);
    }

    [Fact]
    public void ItsRectangleBecomesTheRegionAMouseDragOfTheSameRectangleWould()
    {
        // The keyboard and the mouse meet at BlurRegion.From: the same rectangle on the same picture is
        // the same region of the frame, rounded outward, whichever drew it.
        var displayed = new Size(533.3, 300);
        var box = BlurBox.Centred.Move(3, 10).Resize(2, 9);
        var drawn = box.Drawn(displayed);

        var region = BlurRegion.From(drawn, displayed, new Size(1920, 1080));

        Assert.True(BlurRegion.IsMeaningful(drawn));
        Assert.Equal(region, BlurRegion.From(Rect.Between(drawn.Left, drawn.Top, drawn.Right, drawn.Bottom), displayed, new Size(1920, 1080)));
        Assert.Equal((825L, 540L, 423L, 314L), (region.X, region.Y, region.Width, region.Height));
        Assert.True(BlurRegion.IsInside(region, 1920, 1080));
    }

    [Fact]
    public void OnASmallPictureTheSmallestBoxIsStillBigEnoughToBlur()
    {
        // 2% of a picture drawn 150 x 100 is 3 x 2, under the 4 that BlurRegion.IsMeaningful takes for a
        // wobbled click. A box the technician can see and confirm must never be silently dropped, so it is
        // drawn at least that size, still inside the picture.
        var displayed = new Size(150, 100);
        var smallest = BlurBox.Of(99, 99, 0, 0);

        var drawn = smallest.Drawn(displayed);

        Assert.True(BlurRegion.IsMeaningful(drawn), $"{drawn} would be dropped as a click");
        Assert.Equal(new Rect(146, 96, 4, 4), drawn);
        Assert.True(BlurRegion.IsMeaningful(BlurBox.Of(0, 0, 0, 0).Drawn(displayed)));
    }

    [Fact]
    public void APictureNotYetDrawnHasNoRectangle()
    {
        Assert.Equal(new Rect(0, 0, 0, 0), BlurBox.Centred.Drawn(new Size(0, 0)));
        Assert.False(BlurRegion.IsMeaningful(BlurBox.Centred.Drawn(new Size(0, 300))));
    }

    [Fact]
    public void WhatIsSpokenIsNumbersOnly()
    {
        // INV-10: the box's announcement says where it is in the picture and nothing about what is under it.
        Assert.Equal("Blur box: 43% from the left, 50% from the top, 22% wide, 29% tall", BlurBox.Centred.Move(3, 10).Resize(2, 9).Spoken);
    }

    private static (int X, int Y, int Width, int Height) Parts(BlurBox box) => (box.X, box.Y, box.Width, box.Height);
}
