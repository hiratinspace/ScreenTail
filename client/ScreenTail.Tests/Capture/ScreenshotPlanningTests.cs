using ScreenTail.Core.Capture;

namespace ScreenTail.Tests.Capture;

/// <summary>ST-025: which clicks earn a frame, and how big the frame is stored.</summary>
public sealed class ScreenshotPlanningTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FiveClicksInTheDebounceWindowProduceOneFrame()
    {
        // The acceptance criterion. A double-click, a drag, clicking through a menu: one action, one frame.
        var clock = new ManualTime(At);
        var debouncer = new ClickDebouncer(clock, TimeSpan.FromMilliseconds(400));

        var captured = 0;
        for (var i = 0; i < 5; i++)
        {
            if (debouncer.ShouldCapture())
            {
                captured++;
            }

            clock.Advance(TimeSpan.FromMilliseconds(80));
        }

        Assert.Equal(1, captured);
        Assert.Equal(4, debouncer.Suppressed);
    }

    [Fact]
    public void TheFirstClickOfABurstIsTheOneCaptured()
    {
        // Deliberately the first, not the last: the frame is taken to show what the technician acted on.
        // Waiting for the burst to end would photograph the consequence instead of the cause.
        var clock = new ManualTime(At);
        var debouncer = new ClickDebouncer(clock, TimeSpan.FromMilliseconds(400));

        Assert.True(debouncer.ShouldCapture());
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.False(debouncer.ShouldCapture());
    }

    [Fact]
    public void ClicksFurtherApartEachGetAFrame()
    {
        var clock = new ManualTime(At);
        var debouncer = new ClickDebouncer(clock, TimeSpan.FromMilliseconds(400));

        Assert.True(debouncer.ShouldCapture());
        clock.Advance(TimeSpan.FromMilliseconds(401));
        Assert.True(debouncer.ShouldCapture());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(debouncer.ShouldCapture());
        Assert.Equal(0, debouncer.Suppressed);
    }

    [Fact]
    public void ANewSessionStartsWithNoHistory()
    {
        var clock = new ManualTime(At);
        var debouncer = new ClickDebouncer(clock, TimeSpan.FromMilliseconds(400));
        debouncer.ShouldCapture();

        debouncer.Reset();

        Assert.True(debouncer.ShouldCapture());
        Assert.Equal(0, debouncer.Suppressed);
    }

    [Theory]
    [InlineData(3840, 2160, 1600, 900)]   // 4K, 16:9
    [InlineData(1920, 1080, 1600, 900)]
    [InlineData(2560, 1600, 1600, 1000)]  // 16:10
    [InlineData(2160, 3840, 900, 1600)]   // portrait: the long edge is capped, whichever it is
    public void TheLongEdgeIsCappedAndTheShapeIsKept(int width, int height, int expectedWidth, int expectedHeight)
    {
        var plan = Downscale.For(width, height);

        Assert.Equal(expectedWidth, plan.Width);
        Assert.Equal(expectedHeight, plan.Height);
        Assert.True(plan.Resamples);
        Assert.Equal((double)width / height, (double)plan.Width / plan.Height, 2);
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(800, 600)]
    [InlineData(320, 200)]
    public void ASmallWindowIsLeftAlone(int width, int height)
    {
        // Never upscale: a small dialog stays its own size rather than being blown up into a blur.
        var plan = Downscale.For(width, height);

        Assert.Equal(width, plan.Width);
        Assert.Equal(height, plan.Height);
        Assert.False(plan.Resamples);
        Assert.Equal(1.0, plan.Scale);
    }

    [Fact]
    public void AnAbsurdlyThinWindowStillHasPixels()
    {
        var plan = Downscale.For(4000, 1);

        Assert.Equal(1600, plan.Width);
        Assert.Equal(1, plan.Height);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    public void AWindowWithNoAreaIsRejected(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Downscale.For(width, height));

    [Fact]
    public void TheStoredSizeHonoursATenantsSmallerCap()
    {
        // ST-047 can lower this for a tenant on a tight upload budget.
        var plan = Downscale.For(3840, 2160, maxEdge: 1024);

        Assert.Equal(1024, plan.Width);
        Assert.Equal(576, plan.Height);
    }

    [Fact]
    public void TheTimingBreakdownAddsUpAndReadsPlainly()
    {
        var timing = new CaptureTiming(
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(8),
            TimeSpan.FromMilliseconds(12),
            TimeSpan.FromMilliseconds(60));

        Assert.Equal(120, timing.Total.TotalMilliseconds);
        Assert.Contains("grab 40.0 ms", timing.ToString(), StringComparison.Ordinal);
        Assert.Contains("encode 60.0 ms", timing.ToString(), StringComparison.Ordinal);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
