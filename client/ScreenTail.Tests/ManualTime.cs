namespace ScreenTail.Tests;

/// <summary>
/// A clock the test moves by hand.
///
/// There were two private copies of this, and one of them overrode <see cref="GetUtcNow"/> only. A
/// <see cref="TimeProvider"/> that does not also override <see cref="GetTimestamp"/> and
/// <see cref="TimestampFrequency"/> keeps the real high-resolution timer underneath, so any code measuring
/// with <c>GetElapsedTime</c> silently ignores the fake clock and measures the test runner instead —
/// which is exactly how <c>ActiveTimeExcludesPauses</c> came to assert a band of real milliseconds and
/// fail on a busy CI machine. Overriding all four keeps the two halves of the interface telling the same
/// story.
/// </summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
