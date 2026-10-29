using ScreenTail.Core.Perf;

namespace ScreenTail.Tests.Performance;

/// <summary>A processor clock the test moves by hand, so every reading is the one the test wrote down.</summary>
internal sealed class FakeProcessorTime(int processors) : IProcessorTime
{
    private long _ticks;

    public TimeSpan Used => new(Interlocked.Read(ref _ticks));

    public int Processors { get; } = processors;

    public void Use(TimeSpan cpu) => Interlocked.Add(ref _ticks, cpu.Ticks);
}

/// <summary>Throttles for tests about what a level does, not about how it is reached.</summary>
internal static class Throttles
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A four-core machine's throttle, driven over budget until it reaches <paramref name="level"/>.</summary>
    public static CaptureThrottle At(CaptureLevel level, CaptureThrottleOptions? options = null)
    {
        var cpu = new FakeProcessorTime(processors: 4);
        var time = new ManualTime(Start);
        var throttle = new CaptureThrottle(cpu, time, options);
        throttle.Observe();

        // Every core flat out: as far over budget as a reading can be.
        for (var second = 0; second < 600 && throttle.Level != level; second++)
        {
            cpu.Use(TimeSpan.FromSeconds(cpu.Processors));
            time.Advance(TimeSpan.FromSeconds(1));
            throttle.Observe();
        }

        Assert.Equal(level, throttle.Level);
        return throttle;
    }
}
