using System.Collections.Concurrent;
using ScreenTail.Core.Input;

namespace ScreenTail.Tests.Input;

/// <summary>
/// The thread the input hooks live on, stopped and started again for every session (ST-024, T-33).
///
/// Since the hooks began following the session (ST-031), one hooks object is started and stopped once per
/// session for the whole life of the service. Its stop signal was made once and never renewed, so the
/// second session's message loop found it already set, took the hooks out straight away, and the start
/// still reported success. Every session after the first recorded no clicks and no typing.
///
/// The real loop needs a desktop, so it is tested on the laptop (<c>InputHookTests</c>). Everything about
/// starting, stopping and starting again is here, where it runs on any machine.
/// </summary>
public sealed class RestartablePumpTests
{
    [Fact]
    public async Task ThePumpComesBackForTheSecondSession()
    {
        var loop = new FakeLoop();
        var prepared = new ConcurrentQueue<(Thread Thread, bool Background, bool Unstarted)>();
        await using var pump = new RestartablePump(
            "test pump",
            loop.Body,
            thread => prepared.Enqueue((thread, thread.IsBackground, thread.ThreadState.HasFlag(ThreadState.Unstarted))));
        var ct = TestContext.Current.CancellationToken;

        await pump.StartAsync(ct);
        await pump.StopAsync(ct);
        await pump.StartAsync(ct);

        // The second run starts with a stop signal of its own, unset, so it stays in its loop.
        Assert.Equal([false, false], loop.StoppedOnArrival);
        Assert.True(pump.Running);
        Assert.Equal(1, loop.Live);

        // A new thread for each run, prepared before it starts and never keeping the process alive.
        Assert.Equal(2, prepared.Count);
        Assert.All(prepared, seen => Assert.True(seen.Background && seen.Unstarted));
        Assert.Equal(2, prepared.Select(seen => seen.Thread).Distinct().Count());
    }

    [Fact]
    public async Task EverySessionInOneServiceLifetimeGetsALivePump()
    {
        var loop = new FakeLoop();
        await using var pump = new RestartablePump("test pump", loop.Body);
        var ct = TestContext.Current.CancellationToken;

        for (var session = 0; session < 5; session++)
        {
            await pump.StartAsync(ct);
            Assert.True(pump.Running);
            await pump.StopAsync(ct);
            Assert.False(pump.Running);
        }

        Assert.Equal(5, loop.Runs);
        Assert.DoesNotContain(true, loop.StoppedOnArrival);
        Assert.Equal(0, loop.Live);
    }

    [Fact]
    public async Task APumpThatEndsBeforeItIsReadyFailsTheStart()
    {
        // The caller logs "installed" when the start succeeds. A loop that ended without saying it was ready
        // installed nothing, so the start must not succeed.
        await using var pump = new RestartablePump("test pump", _ => { });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => pump.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(pump.Running);
    }

    [Fact]
    public async Task APumpThatThrowsBeforeItIsReadyFailsTheStartWithItsReason()
    {
        await using var pump = new RestartablePump("test pump", _ => throw new InvalidDataException());

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => pump.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(pump.Running);
    }

    [Fact]
    public async Task APumpThatFailedToStartCanBeStartedOnTheNextSession()
    {
        // HookLifetime retries on the next transition, which is worth something only if a retry runs a
        // fresh loop rather than reporting the dead one.
        var attempts = 0;
        var loop = new FakeLoop();
        await using var pump = new RestartablePump("test pump", run =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidDataException();
            }

            loop.Body(run);
        });
        var ct = TestContext.Current.CancellationToken;

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => pump.StartAsync(ct));
        await pump.StartAsync(ct);

        Assert.True(pump.Running);
        Assert.Equal(1, loop.Live);
    }

    [Fact]
    public async Task APumpThatDiedIsStartedAgainRatherThanReportedAsRunning()
    {
        // A loop can end on its own (a message loop that fails). The next start must run a new one, not
        // return at once because a pump was started earlier.
        var loop = new FakeLoop();
        var attempts = 0;
        await using var pump = new RestartablePump("test pump", run =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                run.Ready();
                return;
            }

            loop.Body(run);
        });
        var ct = TestContext.Current.CancellationToken;

        await pump.StartAsync(ct);
        Assert.True(SpinWait.SpinUntil(() => !pump.Running, TimeSpan.FromSeconds(5)));

        await pump.StartAsync(ct);

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.True(pump.Running);
        Assert.Equal(1, loop.Live);
    }

    [Fact]
    public async Task AFailureAfterReadyStaysOnThePumpThread()
    {
        // An exception escaping a thread ends the process. One that happens after the start was reported
        // ends this run and nothing else; the next session starts a new one.
        var attempts = 0;
        var loop = new FakeLoop();
        await using var pump = new RestartablePump("test pump", run =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                run.Ready();
                throw new InvalidDataException();
            }

            loop.Body(run);
        });
        var ct = TestContext.Current.CancellationToken;

        await pump.StartAsync(ct);
        Assert.True(SpinWait.SpinUntil(() => !pump.Running, TimeSpan.FromSeconds(5)));
        await pump.StopAsync(ct);
        await pump.StartAsync(ct);

        Assert.True(pump.Running);
        Assert.Equal(1, loop.Live);
    }

    [Fact]
    public async Task StartingTwiceWhileRunningIsOnePump()
    {
        var loop = new FakeLoop();
        await using var pump = new RestartablePump("test pump", loop.Body);
        var ct = TestContext.Current.CancellationToken;

        await pump.StartAsync(ct);
        await pump.StartAsync(ct);

        Assert.Equal(1, loop.Runs);
        Assert.Equal(1, loop.Live);
    }

    [Fact]
    public async Task StoppingWithNothingRunningIsHarmless()
    {
        var loop = new FakeLoop();
        await using var pump = new RestartablePump("test pump", loop.Body);
        var ct = TestContext.Current.CancellationToken;

        await pump.StopAsync(ct);
        await pump.StartAsync(ct);
        await pump.StopAsync(ct);
        await pump.StopAsync(ct);

        Assert.False(pump.Running);
        Assert.Equal(1, loop.Runs);
        Assert.Equal(0, loop.Live);
    }

    [Fact]
    public async Task ACancelledStartLeavesNoPumpBehind()
    {
        // The caller that gave up will not remove what it does not know went in, so nothing may stay in.
        using var release = new ManualResetEventSlim();
        var sawStop = false;
        await using var pump = new RestartablePump("test pump", run =>
        {
            _ = WaitHandle.WaitAny([release.WaitHandle, run.Stopping.WaitHandle]);
            if (run.Stopping.IsCancellationRequested)
            {
                sawStop = true;
                return;
            }

            run.Ready();
        });
        using var giveUp = new CancellationTokenSource();

        var starting = pump.StartAsync(giveUp.Token);
        await giveUp.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.False(pump.Running);
        Assert.True(Volatile.Read(ref sawStop));
    }

    [Fact]
    public async Task APumpThatWillNotStopIsNeverJoinedByASecond()
    {
        // Two loops at once would share one set of hook handles. A stop that times out says so, and the
        // next start refuses until the first loop has really ended.
        using var release = new ManualResetEventSlim();
        var runs = 0;
        var live = 0;
        await using var pump = new RestartablePump(
            "test pump",
            run =>
            {
                _ = Interlocked.Increment(ref runs);
                _ = Interlocked.Increment(ref live);
                run.Ready();
                release.Wait();
                _ = Interlocked.Decrement(ref live);
            },
            stopWait: TimeSpan.FromMilliseconds(50));
        var ct = TestContext.Current.CancellationToken;

        await pump.StartAsync(ct);
        _ = await Assert.ThrowsAsync<TimeoutException>(() => pump.StopAsync(ct));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => pump.StartAsync(ct));
        Assert.Equal(1, Volatile.Read(ref runs));

        release.Set();
        await pump.StopAsync(ct);
        release.Reset();
        await pump.StartAsync(ct);

        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Equal(1, Volatile.Read(ref live));
        release.Set();
    }

    [Fact]
    public async Task DisposingStopsThePumpAndRefusesAnother()
    {
        var loop = new FakeLoop();
        var pump = new RestartablePump("test pump", loop.Body);
        var ct = TestContext.Current.CancellationToken;
        await pump.StartAsync(ct);

        await pump.DisposeAsync();
        await pump.DisposeAsync();

        Assert.False(pump.Running);
        Assert.Equal(0, loop.Live);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => pump.StartAsync(ct));
    }

    [Fact]
    public async Task StartsAndStopsFromManyThreadsNeverRunTwoLoopsAtOnce()
    {
        var loop = new FakeLoop();
        await using var pump = new RestartablePump("test pump", loop.Body);
        var ct = TestContext.Current.CancellationToken;

        await Parallel.ForAsync(0, 200, ct, async (i, token) =>
        {
            try
            {
                await (i % 2 == 0 ? pump.StartAsync(token) : pump.StopAsync(token));
            }
            catch (InvalidOperationException)
            {
                // A start that met a run still stopping. Refusing is the point.
            }
        });
        await pump.StopAsync(ct);

        Assert.Equal(1, loop.MostLiveAtOnce);
        Assert.Equal(0, loop.Live);
        Assert.False(pump.Running);
    }

    /// <summary>
    /// Stands in for the message loop: it says it is ready, then waits until its stop signal wakes it,
    /// the way <c>GetMessage</c> waits until the stop posts <c>WM_QUIT</c>.
    /// </summary>
    internal sealed class FakeLoop
    {
        private readonly ConcurrentQueue<bool> _stoppedOnArrival = new();
        private int _runs;
        private int _live;
        private int _mostLive;

        public int Runs => Volatile.Read(ref _runs);

        public int Live => Volatile.Read(ref _live);

        public int MostLiveAtOnce => Volatile.Read(ref _mostLive);

        /// <summary>For each run, whether its stop signal was already set when it began.</summary>
        public bool[] StoppedOnArrival => [.. _stoppedOnArrival];

        public void Body(PumpRun run)
        {
            _ = Interlocked.Increment(ref _runs);
            var live = Interlocked.Increment(ref _live);
            InterlockedMax(ref _mostLive, live);
            try
            {
                _stoppedOnArrival.Enqueue(run.Stopping.IsCancellationRequested);
                using var wake = new ManualResetEventSlim();
                using (run.Stopping.Register(wake.Set))
                {
                    run.Ready();
                    while (!run.Stopping.IsCancellationRequested)
                    {
                        wake.Wait();
                    }
                }
            }
            finally
            {
                _ = Interlocked.Decrement(ref _live);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var seen = Volatile.Read(ref target);
            while (value > seen)
            {
                var previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
