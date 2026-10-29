using ScreenTail.Core.Ipc;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// INV-4 where it is actually decided: capture stops when nothing on screen says it is happening
/// (2026-09-19 review).
///
/// The guard asks whether anything reports that it is showing the indicator. It used to ask how many
/// clients were connected, which is not the same question: a connection is not a pill (2026-09-20).
///
/// The tray icon and the pill live in the UI process, and the service records without them. It starts
/// sessions by itself when a remote tool takes the foreground, it owns the global hotkeys, and nothing
/// anywhere asked whether a UI was attached. A UI that crashed, was quit from its own menu, or was never
/// started at all left the service recording a customer's screen with no indicator of any kind.
///
/// That is invisible capture reached without touching capture — the review's own phrase for it — and it
/// is the thing INV-4 exists to forbid. These tests read the encrypted store afterwards, because what is
/// written is the claim.
/// </summary>
public sealed class IndicatorGuardTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 11, 0, 0, TimeSpan.Zero);
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };

    /// <summary>The guard's own grace, the one a UI restart gets and the pill gets after a start.</summary>
    private static readonly TimeSpan Grace = new IndicatorOptions().Grace;

    /// <summary>How often the UI reports, as <c>HudWindowHolder</c> does.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(2);

    private const double PillWidth = 220;
    private const double PillHeight = 36;
    private MachineHarness? _harness;

    [Fact]
    public async Task CaptureStopsWhenTheLastWindowCloses()
    {
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);

        showing = false;
        await GraceOutAsync(guard, clock, TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
    }

    [Fact]
    public async Task NothingIsWrittenWhileNobodyCanSeeThatItIsRecording()
    {
        // Suppression already means the session writes nothing (INV-6), which is why it is the right
        // mechanism here: the invariant holds the moment this takes effect, with no second rule to keep.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        await GraceOutAsync(guard, clock, ct);

        Assert.False(await machine.TryStageFrameAsync(Frame("f-invisible"), ct));
        Assert.False(await machine.TryRecordEventAsync(new TypingBurstEvent { TsMs = 1_000, CharCount = 12 }, ct));

        var stored = (await _harness!.Store.LoadSessionAsync(machine.SessionId!, ct))!;
        Assert.Equal(0, await _harness.Store.CountPendingFramesAsync(machine.SessionId!, ct));
        Assert.Empty(stored.Events.OfType<TypingBurstEvent>());
    }

    [Fact]
    public async Task AWindowReopeningPutsCaptureBack()
    {
        // The technician's UI crashed and came back. One job, not two notes.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        await GraceOutAsync(guard, clock, ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        showing = true;
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task ARestartingWindowDoesNotInterruptTheSession()
    {
        // The UI reconnects a moment after its own restart. Suppressing on every blink would fill the
        // timeline with intervals that mean nothing and cut a session nobody interrupted.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await guard.TickAsync(ct);
        showing = true;
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.Equal(0, guard.Holds);
    }

    [Fact]
    public async Task TheIntervalIsOnTheTimelineWithItsReason()
    {
        // A gap in a session is something a technician and a customer may both ask about, and "the
        // window was closed" is an answer. A silent gap is not.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = true;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        showing = false;
        await GraceOutAsync(guard, clock, ct);

        var stored = (await _harness!.Store.LoadSessionAsync(machine.SessionId!, ct))!;
        var suppressed = Assert.Single(
            stored.Events.OfType<CaptureStateEvent>(),
            e => e.State == CaptureState.Suppressed);

        Assert.Equal(CaptureStateReason.NoIndicator, suppressed.Reason);
    }

    [Fact]
    public async Task AnAttachedWindowIsAnIndicator()
    {
        var machine = await RecordingAsync();

        Assert.True(new IndicatorGuard(machine, () => true).Indicated);
        Assert.False(new IndicatorGuard(machine, () => false).Indicated);
    }

    [Fact]
    public async Task TheGuardDoesNotResumeASessionTheTechnicianPaused()
    {
        // Holding is this guard's belief, not the session's state. The other guards share the same
        // suppressed state, so it has to be re-derived or this one resumes something it never held.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = false;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        await GraceOutAsync(guard, clock, ct);
        Assert.True(await machine.PauseAsync(ct));

        showing = true;
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Paused, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task AHoldWhoseConditionEndsDuringAPauseDoesNotOutliveTheResume()
    {
        // 2026-10-12 review, T-41. The UI came back while the technician had the session paused. The
        // guard forgot its hold instead of letting go of it, and Resume went back to a suppression that
        // nothing would ever lift.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = false;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        await GraceOutAsync(guard, clock, ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        Assert.True(await machine.PauseAsync(ct));
        showing = true;
        await guard.TickAsync(ct);

        Assert.True(await machine.ResumeAsync(ct));
        Assert.Equal(SessionState.Recording, machine.State);

        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Recording, machine.State);
        Assert.True(await machine.TryStageFrameAsync(Frame("after"), ct));
    }

    [Fact]
    public async Task AnIndicatorStillMissingAfterAPauseKeepsItsHold()
    {
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var guard = new IndicatorGuard(machine, () => false, clock);
        var ct = TestContext.Current.CancellationToken;

        await GraceOutAsync(guard, clock, ct);
        Assert.True(await machine.PauseAsync(ct));
        await guard.TickAsync(ct);
        Assert.True(guard.Holding);

        Assert.True(await machine.ResumeAsync(ct));
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.Equal(1, guard.Holds);
    }

    [Fact]
    public async Task AnIndicatorLostDuringAPauseIsHeldTheMomentItResumes()
    {
        // 2026-10-12 review, round 2. The UI went away during a pause and nothing was held, so Resume went
        // to Recording with no indicator on screen until the next tick, up to 500 ms later (INV-4). The
        // hold is taken during the pause now, and Resume goes straight to Suppressed.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await machine.PauseAsync(ct));
        var guard = new IndicatorGuard(machine, () => false, clock);

        await GraceOutAsync(guard, clock, ct);
        Assert.True(guard.Holding);
        Assert.Equal(SessionState.Paused, machine.State);

        Assert.True(await machine.ResumeAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.False(await machine.TryStageFrameAsync(Frame("f-invisible"), ct));
    }

    [Fact]
    public async Task AnIndicatorThatComesBackAndGoesAgainDuringAPauseIsHeldTheMomentItResumes()
    {
        // The route the T-41 fix opened: the UI comes back during the pause and the hold is let go, then
        // it goes away again for longer than the grace before the technician resumes.
        var clock = new ManualTime(At);
        var machine = await RecordingAsync();
        var showing = false;
        var guard = new IndicatorGuard(machine, () => showing, clock);
        var ct = TestContext.Current.CancellationToken;

        await GraceOutAsync(guard, clock, ct);
        Assert.True(await machine.PauseAsync(ct));
        showing = true;
        await guard.TickAsync(ct);
        Assert.False(guard.Holding);
        showing = false;
        await GraceOutAsync(guard, clock, ct);

        Assert.True(await machine.ResumeAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.Equal(2, guard.Holds);
    }

    [Fact]
    public async Task AGuardWhoseCheckThrowsKeepsGuarding()
    {
        // The 2026-09-19 fail-open, kept closed. The guard's loop caught cancellation and nothing else, so
        // one exception from a pass ended it for the life of the service, and capture carried on with no
        // indicator and nothing watching for one. The loop lives in ResilientLoop now; this runs the real
        // RunAsync through a pass that throws so that inlining a plain loop again goes red (T-39).
        var machine = await RecordingAsync();
        var calls = 0;
        var guard = new IndicatorGuard(
            machine,
            () => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("the pill could not be asked") : false,
            options: new IndicatorOptions { Grace = TimeSpan.FromMilliseconds(20), Tick = TimeSpan.FromMilliseconds(5) });
        var failures = new List<Type>();
        guard.Failed += ex => failures.Add(ex.GetType());
        using var stop = new CancellationTokenSource();

        var running = guard.RunAsync(stop.Token);
        await WaitUntilAsync(() => machine.State == SessionState.Suppressed, running);
        stop.Cancel();
        await running;

        Assert.Equal(1, guard.Failures);
        Assert.Equal(typeof(InvalidOperationException), Assert.Single(failures));
        Assert.True(guard.Holding);
        Assert.True(calls > 1, "the loop stopped asking after the pass that threw");
    }

    // ---- the pill put away between sessions (owner decision, 2026-10-25) -----------------------------
    //
    // Between sessions the technician may put the pill away. The UI then reports a zero-size rectangle,
    // and the next session may start on it. Once a session runs, only the pill counts: it has the grace,
    // counted from the start, to come back, and after that capture is suppressed as for a missing pill.
    // These run the session and the guard on one clock, because the grace is counted from the start.

    [Fact]
    public async Task APillPutAwayBetweenSessionsStillCountsForTheNextStart()
    {
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // A minute of the UI saying the pill is away, every two seconds as it does.
        for (var i = 0; i < 30; i++)
        {
            reports.Report(ui, 0, 0);
            await guard.TickAsync(ct);
            Assert.True(guard.Indicated);
            clock.Advance(ReportEvery);
        }

        Assert.Equal(0, guard.Holds);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
    }

    [Fact]
    public async Task AfterASessionEndsAPillPutAwayCountsForTheNextStartAgain()
    {
        // A session ends in draft_ready or draft_failed, not idle, and that is still between sessions.
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        reports.Report(ui, PillWidth, PillHeight);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        await guard.TickAsync(ct);
        Assert.True(await machine.StopAsync(ct));
        Assert.Equal(SessionState.DraftFailed, machine.State);

        // Longer than the grace, the UI reporting as it does.
        for (var i = 0; i < 5; i++)
        {
            reports.Report(ui, 0, 0);
            await guard.TickAsync(ct);
            clock.Advance(ReportEvery);
        }

        Assert.True(guard.Indicated);
        Assert.Equal(0, guard.Holds);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
    }

    [Fact]
    public async Task APillPutAwayStopsCountingOnceTheSessionStarts()
    {
        // The pill did not come back. The UI goes on saying it is away, and the grace runs from the start:
        // not from the report, which was sent before the start, and not from the guard's first look.
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        reports.Report(ui, 0, 0);
        clock.Advance(ReportEvery);
        Assert.True(guard.Indicated);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        reports.Report(ui, 0, 0);
        Assert.False(guard.Indicated);

        // The guard's first look. Counted from the first report, two seconds before the start, the grace
        // would be over here.
        var firstLook = TimeSpan.FromMilliseconds(1500);
        clock.Advance(firstLook);
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Recording, machine.State);

        reports.Report(ui, 0, 0);
        clock.Advance(Grace - firstLook - TimeSpan.FromMilliseconds(100));
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Recording, machine.State);

        // The grace from the start is over. Counted from the guard's first look it would not be.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
        Assert.False(await machine.TryStageFrameAsync(Frame("f-no-pill"), ct));
    }

    [Fact]
    public async Task APillThatComesBackWithinTheGraceKeepsCaptureGoing()
    {
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        reports.Report(ui, 0, 0);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        clock.Advance(TimeSpan.FromSeconds(1));
        await guard.TickAsync(ct);

        // The UI heard of the session and put the pill back, a moment inside the grace.
        clock.Advance(Grace - TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(100));
        reports.Report(ui, PillWidth, PillHeight);
        for (var i = 0; i < 10; i++)
        {
            await guard.TickAsync(ct);
            clock.Advance(ReportEvery);
            reports.Report(ui, PillWidth, PillHeight);
        }

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.True(guard.Indicated);
        Assert.Equal(0, guard.Holds);
        Assert.True(await machine.TryStageFrameAsync(Frame("f-pill"), ct));
    }

    [Fact]
    public async Task APillPutAwayInTheMiddleOfASessionIsNotThePill()
    {
        // The UI cannot put the pill away while it knows a session runs. It can believe the last session
        // is still the latest thing that happened, if it missed the state change, and then it says the pill
        // is away with the pill hidden. The grace from the start is long over, so capture stops at once.
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        reports.Report(ui, PillWidth, PillHeight);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        for (var i = 0; i < 5; i++)
        {
            await guard.TickAsync(ct);
            clock.Advance(ReportEvery);
            reports.Report(ui, PillWidth, PillHeight);
        }

        reports.Report(ui, 0, 0);
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);

        // And the pill coming back puts capture back.
        reports.Report(ui, PillWidth, PillHeight);
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public void TheServiceCanOnlyBuildTheGuardOverTheReports()
    {
        // The host is in the service, out of this suite's reach. A guard over a plain "something is
        // showing" cannot tell the pill from the pill put away, and wired that way it would quietly undo
        // all of the above, so the only constructor outside Core and its tests takes the reports.
        var constructor = Assert.Single(typeof(IndicatorGuard).GetConstructors());

        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IndicatorReports));
    }

    [Fact]
    public async Task ASessionSuppressedForWantOfThePillStaysSuppressedWhileThePillIsAway()
    {
        // Suppressed is still a session under way. Read as between sessions, the zero-size report would
        // count again, lift the hold, and the next pass would take it again: capture flapping on and off
        // every tick with no pill on screen (2026-10-29 review).
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        reports.Report(ui, 0, 0);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        await guard.TickAsync(ct);
        clock.Advance(Grace);
        reports.Report(ui, 0, 0);
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            if (i % 4 == 0)
            {
                reports.Report(ui, 0, 0);
            }

            await guard.TickAsync(ct);
            Assert.Equal(SessionState.Suppressed, machine.State);
            Assert.Equal(1, guard.Holds);
        }
    }

    [Fact]
    public async Task APausedSessionWithThePillAwayResumesSuppressed()
    {
        // Paused is a session under way too. The hold is taken during the pause, so Resume goes straight
        // to Suppressed and never records a moment with no pill on screen.
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        reports.Report(ui, 0, 0);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        Assert.True(await machine.PauseAsync(ct));

        for (var elapsed = TimeSpan.Zero; elapsed <= Grace + ReportEvery; elapsed += ReportEvery)
        {
            reports.Report(ui, 0, 0);
            await guard.TickAsync(ct);
            clock.Advance(ReportEvery);
        }

        Assert.True(guard.Holding);
        reports.Report(ui, 0, 0);
        await guard.TickAsync(ct);
        Assert.True(await machine.ResumeAsync(ct));
        Assert.Equal(SessionState.Suppressed, machine.State);

        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.False(await machine.TryStageFrameAsync(Frame("f-paused-no-pill"), ct));
    }

    [Fact]
    public async Task APillPutAwayAfterTheUiWentQuietStillCountsFromTheStart()
    {
        // The pill was up at the start, then the UI went away and came back saying the pill is away. The
        // guard first noticed when the reports stopped, but the pill has not been proved on screen since
        // the start either way, and the earlier of the two times is the one that counts.
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        reports.Report(ui, PillWidth, PillHeight);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await guard.TickAsync(ct);

        reports.Forget(ui);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await guard.TickAsync(ct);
        reports.Report(Guid.NewGuid(), 0, 0);
        clock.Advance(Grace - TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(100));
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Recording, machine.State);

        // The grace from the start is over; from the first look it would not be for another second.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task ANewSessionDoesNotInheritTheLastOnesMissingPill()
    {
        // The last session lost its pill a moment before it was stopped, and the next one started on the
        // pill put away, all between two passes. The new session gets its own grace from its own start:
        // carrying the old time over would suppress it on its first pass.
        var clock = new ManualTime(At);
        var machine = await IdleAsync(clock);
        var reports = new IndicatorReports(clock);
        var guard = new IndicatorGuard(machine, reports, clock);
        var ui = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        reports.Report(ui, PillWidth, PillHeight);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        await guard.TickAsync(ct);
        clock.Advance(TimeSpan.FromSeconds(10));
        reports.Forget(ui);
        await guard.TickAsync(ct);

        clock.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.True(await machine.StopAsync(ct));
        reports.Report(ui, 0, 0);
        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(500));
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.Equal(0, guard.Holds);

        // Its own grace still ends it.
        clock.Advance(Grace);
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    /// <summary>
    /// Waits for the loop to reach a state, failing fast if the loop ends first, which is what the
    /// fail-open looks like from outside: RunAsync completes while the session is still Recording.
    /// </summary>
    internal static async Task WaitUntilAsync(Func<bool> condition, Task loop)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        while (!condition())
        {
            var finished = await Task.WhenAny(loop, deadline, Task.Delay(5, TestContext.Current.CancellationToken));
            if (finished == loop)
            {
                await loop;
                Assert.Fail("the guard's loop ended instead of carrying on past the pass that threw");
            }

            Assert.NotSame(deadline, finished);
        }
    }

    /// <summary>
    /// Ticks once to notice the indicator is gone, waits past the grace, and ticks again.
    ///
    /// Two ticks because that is what the running loop does: the first pass records when the last window
    /// went, and only a later one can say it has been gone long enough. A test that suppressed on the
    /// first tick would be testing a guard with no grace period at all.
    /// </summary>
    private static async Task GraceOutAsync(IndicatorGuard guard, ManualTime clock, CancellationToken ct)
    {
        await guard.TickAsync(ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        await guard.TickAsync(ct);
    }

    /// <summary>A session machine with nothing running, on the test's clock.</summary>
    private async Task<SessionMachine> IdleAsync(ManualTime clock)
    {
        _harness = await MachineHarness.StartAsync(clock);
        return _harness.Machine;
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null));
        return _harness.Machine;
    }

    private static StagedFrame Frame(string id) => new(id, 1_000, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA });

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
