namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// A clock the test moves by hand, so a ninety-second grace expires in a line rather than a minute and a
/// half. The same shape as the Mac suite's, which this project cannot reference.
/// </summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
