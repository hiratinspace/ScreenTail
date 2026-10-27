using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// Quit, after the Review panes have written their last (T-169).
///
/// Quit closes the shell window, waits for what its Review pane still has to write (the last of the note,
/// a delete inside its undo window) for a bounded time, and then shuts down whatever happened. A window
/// closed a moment before Quit is waited for too, and a second Quit waits on the first rather than
/// shutting down at once.
/// </summary>
public sealed class ShellQuitTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task APaneThatFinishesLetsQuitShutDownAtOnce()
    {
        var clock = new HandClock();
        var quit = new ShellQuit(clock, Bound);
        var released = new TaskCompletionSource();
        var shutdowns = 0;

        var quitting = quit.QuitAsync(() => quit.WindowClosed(released.Task), () => shutdowns++);
        Assert.Equal(0, shutdowns);

        released.SetResult();
        await quitting;

        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task APaneThatNeverFinishesIsGivenUpOnAtTheBound()
    {
        var clock = new HandClock();
        var quit = new ShellQuit(clock, Bound);
        var shutdowns = 0;

        var quitting = quit.QuitAsync(() => quit.WindowClosed(new TaskCompletionSource().Task), () => shutdowns++);
        clock.Advance(Bound - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, shutdowns);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await quitting;

        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ShutdownRunsWhenThePaneFails()
    {
        var quit = new ShellQuit(new HandClock(), Bound);
        var shutdowns = 0;

        await quit.QuitAsync(() => quit.WindowClosed(Task.FromException(new IOException("the pipe went"))), () => shutdowns++);

        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ShutdownRunsWhenClosingTheWindowFails()
    {
        // The service down, or the window already half gone: Quit is still Quit.
        var quit = new ShellQuit(new HandClock(), Bound);
        var shutdowns = 0;

        await quit.QuitAsync(() => throw new InvalidOperationException("the window is closing already"), () => shutdowns++);

        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task AWindowClosedJustBeforeQuitIsWaitedFor()
    {
        // The shell window closed by its X, then Quit from the tray a moment later, with no window open.
        // The closed window's pane was still writing, and Quit used to shut down without it.
        var quit = new ShellQuit(new HandClock(), Bound);
        var earlier = new TaskCompletionSource();
        quit.WindowClosed(earlier.Task);
        var shutdowns = 0;

        var quitting = quit.QuitAsync(() => { }, () => shutdowns++);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, shutdowns);

        earlier.SetResult();
        await quitting;
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task TheEarlierWindowAndTheOpenOneAreBothWaitedFor()
    {
        var quit = new ShellQuit(new HandClock(), Bound);
        var earlier = new TaskCompletionSource();
        var open = new TaskCompletionSource();
        quit.WindowClosed(earlier.Task);
        var shutdowns = 0;

        var quitting = quit.QuitAsync(() => quit.WindowClosed(open.Task), () => shutdowns++);
        earlier.SetResult();
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, shutdowns);

        open.SetResult();
        await quitting;
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ASecondQuitWaitsOnTheFirstRatherThanShuttingDownAtOnce()
    {
        var quit = new ShellQuit(new HandClock(), Bound);
        var released = new TaskCompletionSource();
        var closes = 0;
        var shutdowns = 0;

        var first = quit.QuitAsync(() => { closes++; quit.WindowClosed(released.Task); }, () => shutdowns++);
        var second = quit.QuitAsync(() => closes++, () => shutdowns++);

        Assert.Same(first, second);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, shutdowns);
        released.SetResult();
        await second;

        Assert.Equal(1, closes);
        Assert.Equal(1, shutdowns);
    }

    /// <summary>A clock whose timers fire only when the test moves it.</summary>
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
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var until = _ticks + by.Ticks;
            while (true)
            {
                HandTimer? next;
                lock (_timers)
                {
                    next = _timers.Where(t => t.DueAt is { } due && due <= until).OrderBy(t => t.DueAt).FirstOrDefault();
                }

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
