namespace ScreenTail.Tests;

/// <summary>
/// A clock the test moves by hand whose timers fire as it moves (ST-072).
///
/// <see cref="ManualTime"/> moves the timestamps but leaves <see cref="TimeProvider.CreateTimer"/> on the
/// real clock, so a component that ticks would tick in real seconds, or not at all, while the test watched
/// a fake one. Here a timer is due at a fake time and fires, on the thread that calls
/// <see cref="Advance"/>, once for every period the move passes over. Nothing waits for real time.
///
/// <paramref name="jitter"/>, when given, moves each arming's due time by what it returns for that arming
/// (counted from zero), the way Windows' timers land a step of its interrupt clock early or late.
/// </summary>
internal sealed class TickingTime(DateTimeOffset start, Func<int, TimeSpan>? jitter = null) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks = start.UtcTicks;
    private readonly Func<int, TimeSpan>? _jitter = jitter;
    private int _armings;

    /// <summary>Timers made and not yet disposed, armed or not.</summary>
    public int Live
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Timers that are due to fire at some point: the count of things that would wake a core.</summary>
    public int Armed
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Volatile.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock, firing every timer due on the way in the order they fall due.</summary>
    public void Advance(TimeSpan by)
    {
        var end = Volatile.Read(ref _ticks) + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } due && due <= end).MinBy(t => t.Due);
                if (next is null)
                {
                    break;
                }

                Volatile.Write(ref _ticks, Math.Max(_ticks, next.Due!.Value));
                next.Due = next.Period is { } period ? next.Due + period : null;
            }

            next.Fire();
        }

        Volatile.Write(ref _ticks, end);
    }

    private sealed class ManualTimer(TickingTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due { get; set; }

        public long? Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                var off = dueTime == Timeout.InfiniteTimeSpan || owner._jitter is null ? TimeSpan.Zero : owner._jitter(owner._armings++);
                // At least a millisecond on, as a real timer is: a timer re-armed for "now" from its own
                // callback would otherwise fire for ever at one instant and the move would never end.
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + Math.Max(TimeSpan.TicksPerMillisecond, dueTime.Ticks + off.Ticks);
                Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? null : period.Ticks;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                _ = owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
