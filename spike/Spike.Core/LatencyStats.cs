namespace ScreenTail.Spike.Core;

/// <summary>
/// Fixed-capacity latency recorder safe to call from a low-level hook callback:
/// <see cref="Add"/> never allocates or locks.
/// </summary>
public sealed class LatencyStats
{
    private readonly double[] _samples;
    private int _count;

    public LatencyStats(int capacity = 2_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _samples = new double[capacity];
    }

    /// <summary>Samples offered, including any that did not fit in the buffer.</summary>
    public int Offered => Volatile.Read(ref _count);

    public void Add(double milliseconds)
    {
        var index = Interlocked.Increment(ref _count) - 1;
        if (index < _samples.Length)
        {
            _samples[index] = milliseconds;
        }
    }

    public LatencySummary Summarize(double thresholdMs)
    {
        var stored = Math.Min(Offered, _samples.Length);
        var sorted = _samples.AsSpan(0, stored).ToArray();
        Array.Sort(sorted);

        if (sorted.Length == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0, 0, thresholdMs);
        }

        var over = 0;
        foreach (var sample in sorted)
        {
            if (sample >= thresholdMs)
            {
                over++;
            }
        }

        return new LatencySummary(
            sorted.Length,
            Percentile(sorted, 50),
            Percentile(sorted, 95),
            Percentile(sorted, 99),
            sorted[^1],
            over,
            thresholdMs);
    }

    /// <summary>Nearest-rank percentile over an ascending-sorted array.</summary>
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            throw new ArgumentException("No samples.", nameof(sorted));
        }

        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

public sealed record LatencySummary(
    int Count,
    double P50,
    double P95,
    double P99,
    double Max,
    int OverThreshold,
    double ThresholdMs)
{
    /// <summary>
    /// ST-001 AC1 read strictly: every sample under the threshold, not just p99.
    /// A single GC pause inside a managed hook callback is exactly the risk this spike exists to surface.
    /// </summary>
    public bool Passes => Count > 0 && Max < ThresholdMs;
}
