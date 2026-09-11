namespace ScreenTail.Spike.Core.Tests;

public class LatencyStatsTests
{
    [Fact]
    public void Percentiles_UseNearestRank()
    {
        var stats = new LatencyStats(1000);
        for (var i = 1; i <= 100; i++)
        {
            stats.Add(i);
        }

        var summary = stats.Summarize(thresholdMs: 1000);

        Assert.Equal(100, summary.Count);
        Assert.Equal(50, summary.P50);
        Assert.Equal(95, summary.P95);
        Assert.Equal(99, summary.P99);
        Assert.Equal(100, summary.Max);
    }

    [Fact]
    public void Passes_RequiresEverySampleUnderThreshold()
    {
        var stats = new LatencyStats(10);
        stats.Add(0.1);
        stats.Add(4.9);
        Assert.True(stats.Summarize(5.0).Passes);

        stats.Add(5.0);
        var summary = stats.Summarize(5.0);

        Assert.False(summary.Passes);
        Assert.Equal(1, summary.OverThreshold);
    }

    [Fact]
    public void Empty_DoesNotPass()
    {
        var summary = new LatencyStats(10).Summarize(5.0);

        Assert.Equal(0, summary.Count);
        Assert.False(summary.Passes);
    }

    [Fact]
    public void Overflow_CountsOfferedButKeepsCapacity()
    {
        var stats = new LatencyStats(2);
        stats.Add(1);
        stats.Add(2);
        stats.Add(3);

        Assert.Equal(3, stats.Offered);
        Assert.Equal(2, stats.Summarize(5).Count);
    }

    [Fact]
    public void ConcurrentAdds_AreAllRecorded()
    {
        var stats = new LatencyStats(20_000);

        Parallel.For(0, 10_000, i => stats.Add(i % 7));

        Assert.Equal(10_000, stats.Summarize(100).Count);
    }
}
