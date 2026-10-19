namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// A clock that never makes a test wait. Every timer it is asked for is written down, moves the clock on
/// by its due time, and fires at once, so a test can say how long a retry would have waited without
/// spending that long.
/// </summary>
internal sealed class RecordedWaits : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _waits = [];
    private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (_gate)
            {
                return [.. _waits];
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            _waits.Add(dueTime);
            _now += dueTime;
        }

        // On the pool rather than here: the caller has not finished setting up the timer it asked for.
        _ = ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
        return new Fired();
    }

    private sealed class Fired : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
