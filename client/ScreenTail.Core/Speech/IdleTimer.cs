namespace ScreenTail.Core.Speech;

/// <summary>
/// Says when something has gone unused for a while, so whatever is expensive about it can be let go of
/// (ST-027).
///
/// Built for the speech model. whisper.cpp holds 150 to 300 MB once a model is loaded, against ST-031's
/// 600 MB for the whole of ScreenTail, and after the day's first session it stayed resident until the
/// service stopped (2026-10-12 review). One timer, armed again on every use and fired once when it runs
/// out: nothing polls.
///
/// <b>It releases nothing itself.</b> The owner is handed the use count the timer saw when it fired and
/// compares it with <see cref="Uses"/> under its own lock. A use that landed between the firing and the
/// lock means the thing is busy again, and the owner leaves it alone; the use re-armed the timer, so the
/// question is asked again later. That is what keeps a release from racing a use without this class
/// knowing anything about the owner's lock.
/// </summary>
public sealed class IdleTimer : IDisposable
{
    private readonly TimeSpan _after;
    private readonly ITimer _timer;
    private long _uses;
    private bool _disposed;

    /// <param name="after">How long without a use before <paramref name="fire"/> is called.</param>
    /// <param name="fire">
    /// Called once per idle stretch, on a timer thread, with the use count at that moment. Must not throw.
    /// </param>
    public IdleTimer(TimeSpan after, Action<long> fire, TimeProvider? time = null)
    {
        if (after <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(after), after, "An idle period must be positive.");
        }

        ArgumentNullException.ThrowIfNull(fire);
        _after = after;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => fire(Volatile.Read(ref _uses)),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>How many times the thing has been used. What the owner compares the firing's count with.</summary>
    public long Uses => Volatile.Read(ref _uses);

    /// <summary>The thing was just used: the idle period starts again from now.</summary>
    public void Touch()
    {
        _ = Interlocked.Increment(ref _uses);
        if (!_disposed)
        {
            _ = _timer.Change(_after, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
