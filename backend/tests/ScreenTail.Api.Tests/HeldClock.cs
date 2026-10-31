using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ScreenTail.Api.Tests;

/// <summary>
/// A clock that stands still until the test moves it, for a service or a host whose deadlines a test
/// means to land on cue. A service that takes a <see cref="TimeProvider"/> is handed it directly
/// (SummarizationServiceTests). In a host, <see cref="Install"/> puts it in place of the one Program.cs
/// registers, so everything the service times with it (the publish deadline, when a request arrived, a
/// provider's wait before trying again) reads this clock instead of the machine's.
///
/// A timer it hands out fires inside <see cref="Advance"/>, on the test's thread, once the clock reaches
/// its due time, and never by itself. So a deadline lands where the test puts it rather than wherever a
/// busy machine happened to be when a real one ran out. Elapsed time is read from the same clock:
/// <see cref="GetTimestamp"/> counts its ticks, so a request that arrived before an
/// <see cref="Advance"/> of a second and a half has spent a second and a half.
/// </summary>
internal sealed class HeldClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Held> _timers = [];
    private readonly List<TaskCompletionSource> _stampWaiters = [];
    private DateTimeOffset _now = TimeProvider.System.GetUtcNow();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>The clock's ticks, so elapsed time is what <see cref="Advance"/> moved and nothing else.</summary>
    public override long GetTimestamp()
    {
        long ticks;
        TaskCompletionSource[] waiting;
        lock (_gate)
        {
            ticks = _now.UtcTicks;
            waiting = [.. _stampWaiters];
            _stampWaiters.Clear();
        }

        foreach (var waiter in waiting)
        {
            _ = waiter.TrySetResult();
        }

        return ticks;
    }

    /// <summary>
    /// Done once something next reads a timestamp. In the service that is a request arriving
    /// (<c>RequestStart</c>), so a test that waits for it before moving the clock knows the request
    /// arrived before the move.
    /// </summary>
    public Task NextTimestamp()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _stampWaiters.Add(waiter);
        }

        return waiter.Task;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new Held(this, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on and fires, on this thread and in the order they fall due, every timer that is now due.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTimeOffset until;
        lock (_gate)
        {
            until = _now + by;
        }

        while (true)
        {
            Held? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt <= until).MinBy(t => t.DueAt);
                if (next is null)
                {
                    _now = until;
                    return;
                }

                _now = next.DueAt;
                next.Rearm();
            }

            next.Fire();
        }
    }

    /// <summary>Puts this clock in place of the one Program.cs registers.</summary>
    public void Install(IServiceCollection services)
    {
        _ = services.RemoveAll<TimeProvider>();
        _ = services.AddSingleton<TimeProvider>(this);
    }

    private sealed class Held(HeldClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                _ = clock._timers.Remove(this);
                _period = period;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock._now + dueTime;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        /// <summary>Called under the clock's lock as it fires: a periodic timer is due again a period on, a one-shot timer is done.</summary>
        public void Rearm()
        {
            _ = clock._timers.Remove(this);
            if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero)
            {
                DueAt += _period;
                clock._timers.Add(this);
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                _ = clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
