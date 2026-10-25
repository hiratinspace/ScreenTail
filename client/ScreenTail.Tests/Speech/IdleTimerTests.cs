using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// The timing half of letting the speech model go when nobody has spoken for a while (2026-10-12 review,
/// E-9). The model itself needs Windows and a model file; what can be argued about here is when the owner
/// is told, and what it is told, so that a release never races a use.
/// </summary>
public sealed class IdleTimerTests
{
    private static readonly TimeSpan After = TimeSpan.FromMinutes(5);

    [Fact]
    public void NothingFiresUntilTheIdlePeriodHasPassedSinceTheLastUse()
    {
        var clock = new HandClock();
        var fired = new List<long>();
        using var idle = new IdleTimer(After, fired.Add, clock);

        idle.Touch();
        clock.Advance(After - TimeSpan.FromSeconds(1));
        Assert.Empty(fired);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([1L], fired);
    }

    [Fact]
    public void AUseRestartsTheClock()
    {
        // A segment transcribed four minutes into the idle period is a session still going. The model
        // stays until five minutes have passed since that one.
        var clock = new HandClock();
        var fired = new List<long>();
        using var idle = new IdleTimer(After, fired.Add, clock);

        idle.Touch();
        clock.Advance(TimeSpan.FromMinutes(4));
        idle.Touch();
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Empty(fired);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([2L], fired);
    }

    [Fact]
    public void ItFiresOncePerIdleStretchAndNotAgainUntilTheNextUse()
    {
        // One timer, not a poll: an hour of nothing is one firing, and the model is not asked about every
        // five minutes all afternoon.
        var clock = new HandClock();
        var fired = new List<long>();
        using var idle = new IdleTimer(After, fired.Add, clock);

        idle.Touch();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(fired);

        idle.Touch();
        clock.Advance(After);
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void SomethingNeverUsedNeverFires()
    {
        var clock = new HandClock();
        var fired = new List<long>();
        using var idle = new IdleTimer(After, fired.Add, clock);

        clock.Advance(TimeSpan.FromHours(8));

        Assert.Empty(fired);
    }

    [Fact]
    public void TheCountHandedOverTellsALaterUseApart()
    {
        // The owner takes its lock after the firing and compares. A use that landed in between shows as a
        // different count, and the owner leaves the model where it is.
        var clock = new HandClock();
        long? handed = null;
        using var idle = new IdleTimer(After, uses => handed = uses, clock);

        idle.Touch();
        clock.Advance(After);
        Assert.Equal(1L, handed);
        Assert.Equal(handed, idle.Uses);

        idle.Touch();
        Assert.NotEqual(handed, idle.Uses);
    }

    [Fact]
    public void AnIdlePeriodMustBePositive()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new IdleTimer(TimeSpan.Zero, _ => { }));
    }

    [Fact]
    public void TouchingAfterDisposalIsHarmless()
    {
        var clock = new HandClock();
        var fired = new List<long>();
        var idle = new IdleTimer(After, fired.Add, clock);
        idle.Touch();
        idle.Dispose();

        idle.Touch();
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(fired);
    }

    /// <summary>A clock whose timers fire only when the test moves it, in due order.</summary>
    private sealed class HandClock : TimeProvider
    {
        private readonly List<HandTimer> _timers = [];
        private long _ticks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HandTimer(this, callback, state);
            _ = timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var until = _ticks + by.Ticks;
            while (true)
            {
                var next = _timers.Where(t => t.DueAt is { } due && due <= until).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                _ticks = next.DueAt!.Value;
                next.Fire();
            }

            _ticks = until;
        }

        private sealed class HandTimer(HandClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => DueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
