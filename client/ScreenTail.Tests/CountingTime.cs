namespace ScreenTail.Tests;

/// <summary>
/// The system clock, with every timer made through it counted (ST-031).
///
/// A timer is a wakeup: each tick takes a core out of a low-power state to run a callback, and a loop
/// that ticks while there is nothing to do is a battery cost a technician pays all day. Counting the
/// timers a component makes while idle is how a test says "this does not poll" without timing anything.
///
/// <see cref="Frozen"/> timers never fire, for the other half of the claim: that something happens
/// because an event arrived, not because a tick came round. A test that passes with a frozen clock is
/// one that no tick could have passed.
/// </summary>
internal sealed class CountingTime(bool frozen = false) : TimeProvider
{
    private int _created;
    private int _live;

    /// <summary>Timers made through this clock so far.</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>Timers made and not yet disposed.</summary>
    public int Live => Volatile.Read(ref _live);

    public bool Frozen { get; } = frozen;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _ = Interlocked.Increment(ref _created);
        _ = Interlocked.Increment(ref _live);
        var inner = Frozen
            ? base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
            : base.CreateTimer(callback, state, dueTime, period);
        return new Counted(inner, this);
    }

    private sealed class Counted(ITimer inner, CountingTime owner) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            owner.Frozen || inner.Change(dueTime, period);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = Interlocked.Decrement(ref owner._live);
            }

            inner.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = Interlocked.Decrement(ref owner._live);
            }

            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
