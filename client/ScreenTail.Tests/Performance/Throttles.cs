using ScreenTail.Core.Perf;

namespace ScreenTail.Tests.Performance;

/// <summary>A processor clock the test moves by hand, so every reading is the one the test wrote down.</summary>
internal sealed class FakeProcessorTime(int processors) : IProcessorTime
{
    private long _ticks;
    private int _reads;

    public TimeSpan Used
    {
        get
        {
            _ = Interlocked.Increment(ref _reads);
            return new(Interlocked.Read(ref _ticks));
        }
    }

    /// <summary>How many readings have been taken, so a test can tell a loop has had its turn.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public int Processors { get; } = processors;

    public void Use(TimeSpan cpu) => Interlocked.Add(ref _ticks, cpu.Ticks);
}

/// <summary>A four-core machine's throttle over a fake processor and a hand-moved clock.</summary>
internal sealed class ThrottleRig
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    public ThrottleRig(CaptureThrottleOptions? options = null)
    {
        Throttle = new CaptureThrottle(Cpu, Time, options);
        Throttle.Observe();
    }

    public FakeProcessorTime Cpu { get; } = new(processors: 4);

    public ManualTime Time { get; } = new(Start);

    public CaptureThrottle Throttle { get; }

    /// <summary>Every core flat out, a reading a second, until the throttle reaches <paramref name="level"/>.</summary>
    public void DriveTo(CaptureLevel level)
    {
        for (var second = 0; second < 600 && Throttle.Level != level; second++)
        {
            Cpu.Use(TimeSpan.FromSeconds(Cpu.Processors));
            Time.Advance(TimeSpan.FromSeconds(1));
            Throttle.Observe();
        }

        Assert.Equal(level, Throttle.Level);
    }
}

/// <summary>Throttles for tests about what a level does, not about how it is reached.</summary>
internal static class Throttles
{
    /// <summary>A four-core machine's throttle, driven over budget until it reaches <paramref name="level"/>.</summary>
    public static CaptureThrottle At(CaptureLevel level, CaptureThrottleOptions? options = null)
    {
        var rig = new ThrottleRig(options);
        rig.DriveTo(level);
        return rig.Throttle;
    }
}
