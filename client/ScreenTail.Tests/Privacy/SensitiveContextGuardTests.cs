using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-041, the half of the login criterion that is about the product rather than the class: the worker
/// raising <c>SensitiveContextSeen</c> has to end with capture actually off (INV-6). Before this guard the
/// event went nowhere, so a sign-in screen was marked and then screenshotted again on the next click.
/// </summary>
public sealed class SensitiveContextGuardTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private static readonly DateTimeOffset At = new(2026, 9, 13, 13, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(At);
    private MachineHarness? _harness;

    [Fact]
    public async Task ASightingStopsCaptureAndTheWindowEndingStartsItAgain()
    {
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
        Assert.Equal(1, guard.Holds);

        // Still inside the ten seconds: capture stays off.
        _time.Advance(LoginScreenHeuristic.Suppression - TimeSpan.FromSeconds(1));
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Suppressed, machine.State);

        _time.Advance(TimeSpan.FromSeconds(2));
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
        Assert.Equal(1, guard.Holds);
    }

    [Fact]
    public async Task ALoginScreenThatStaysOnScreenKeepsCaptureOff()
    {
        // The case a restarting timer would get wrong: sightings keep arriving, and each one has to push
        // the window out rather than start a second hold that races the first one's resume.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);

        for (var i = 0; i < 5; i++)
        {
            guard.Seen(LoginScreenHeuristic.Suppression);
            await guard.TickAsync(TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(6));
            await guard.TickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SessionState.Suppressed, machine.State);
        }

        Assert.Equal(1, guard.Holds);

        _time.Advance(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task TheGuardDoesNotResumeASessionTheTechnicianPaused()
    {
        // Suppressed and paused are different states with different owners. If the technician pauses while
        // a hold is running, the window ending must not hand capture back — only they can do that.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(await machine.PauseAsync(TestContext.Current.CancellationToken));

        _time.Advance(LoginScreenHeuristic.Suppression * 2);
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Paused, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task ASightingWithNoSessionRunningHoldsNothing()
    {
        // Nothing to suppress, so nothing to hold. A hold claimed here would suppress the next session,
        // which nobody suppressed.
        _harness = await MachineHarness.StartAsync();
        var guard = new SensitiveContextGuard(_harness.Machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        Assert.False(await guard.TickAsync(TestContext.Current.CancellationToken));

        Assert.False(guard.Holding);
        Assert.Equal(0, guard.Holds);
        Assert.Equal(SessionState.Idle, _harness.Machine.State);
    }

    [Fact]
    public async Task ASightingDuringAPauseIsHeldTheMomentItResumes()
    {
        // 2026-10-12 review, round 2. A sighting during a pause used to hold nothing, so Resume went to
        // Recording with the credential prompt on screen until the next tick, up to 250 ms later. The
        // hold is taken during the pause now (the session stays paused, and only this guard's own hold
        // is in the set, so its next tick lets go of nobody else's).
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await machine.PauseAsync(ct));
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        Assert.True(await guard.TickAsync(ct));

        Assert.True(guard.Holding);
        Assert.Equal(1, guard.Holds);
        Assert.Equal(SessionState.Paused, machine.State);

        Assert.True(await machine.ResumeAsync(ct));
        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task AWindowThatEndsAndOpensAgainDuringAPauseIsHeldTheMomentItResumes()
    {
        // The route the T-41 fix opened: the window ends during the pause and the hold is let go, then a
        // second sighting arrives before the technician resumes.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);
        var ct = TestContext.Current.CancellationToken;

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(ct);
        Assert.True(await machine.PauseAsync(ct));
        _time.Advance(LoginScreenHeuristic.Suppression * 2);
        await guard.TickAsync(ct);
        Assert.False(guard.Holding);
        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(ct);

        Assert.True(await machine.ResumeAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.Equal(2, guard.Holds);
    }

    [Fact]
    public async Task AHoldWhoseConditionEndsDuringAPauseDoesNotOutliveTheResume()
    {
        // 2026-10-12 review, T-41. The login screen's window ran out while the technician had paused.
        // The guard forgot its hold instead of letting go of it, and Resume went back to a suppression
        // that nothing would ever lift.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);
        var ct = TestContext.Current.CancellationToken;

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        Assert.True(await machine.PauseAsync(ct));
        _time.Advance(LoginScreenHeuristic.Suppression * 2);
        await guard.TickAsync(ct);

        Assert.True(await machine.ResumeAsync(ct));
        Assert.Equal(SessionState.Recording, machine.State);

        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task AWindowStillOpenAfterAPauseKeepsItsHold()
    {
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);
        var ct = TestContext.Current.CancellationToken;

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(ct);
        Assert.True(await machine.PauseAsync(ct));
        _time.Advance(TimeSpan.FromSeconds(1));
        await guard.TickAsync(ct);
        Assert.True(guard.Holding);

        Assert.True(await machine.ResumeAsync(ct));
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.Equal(1, guard.Holds);
    }

    [Fact]
    public async Task AHoldThatEndsDuringAPauseLeavesAnotherGuardsHoldInPlace()
    {
        // Letting go while paused must be this guard letting go of its own hold and nobody else's.
        var machine = await RecordingAsync();
        var sensitive = new SensitiveContextGuard(machine, _time);
        var showing = false;
        var indicator = new IndicatorGuard(machine, () => showing, _time);
        var ct = TestContext.Current.CancellationToken;

        sensitive.Seen(LoginScreenHeuristic.Suppression);
        await sensitive.TickAsync(ct);
        await indicator.TickAsync(ct);
        _time.Advance(TimeSpan.FromSeconds(5));
        await indicator.TickAsync(ct);
        Assert.True(indicator.Holding);

        Assert.True(await machine.PauseAsync(ct));
        _time.Advance(LoginScreenHeuristic.Suppression);
        await sensitive.TickAsync(ct);
        await indicator.TickAsync(ct);

        Assert.True(await machine.ResumeAsync(ct));
        Assert.Equal(SessionState.Suppressed, machine.State);

        showing = true;
        await indicator.TickAsync(ct);
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task ANewSessionInsideTheWindowIsSuppressedToo()
    {
        // 2026-10-12 review, T-50. A session stopped and started again inside the ten seconds. A guard
        // that trusted its own memory of holding saw "already holding", did nothing, and the credential
        // prompt still on screen was captured for the whole of the new session.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);
        var ct = TestContext.Current.CancellationToken;

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        Assert.True(await machine.StopAsync(ct));
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        _time.Advance(TimeSpan.FromSeconds(1));
        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
        Assert.Equal(2, guard.Holds);
    }

    [Fact]
    public async Task ASuppressionTheStoreRefusedIsTriedAgainOnTheNextTick()
    {
        // 2026-10-12 review, round 2. The state write failed, the session stayed Recording, and the hold
        // stayed in the set. The guard asked the session, was told it held, and never tried again, so the
        // credential prompt was captured for as long as it stayed on screen (INV-6).
        var ct = TestContext.Current.CancellationToken;
        _harness = await MachineHarness.StartAsync();
        var (store, gate) = SuppressionHoldsTests.GatedStore.Over(_harness.Store);
        await using var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(TimeSpan.FromMinutes(1));
        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => guard.TickAsync(ct));
        Assert.False(guard.Holding);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await guard.TickAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
    }

    [Fact]
    public async Task AnEndTheStoreRefusedIsTriedAgainOnTheNextTick()
    {
        // The mirror. The hold left the set before the write that failed, the guard saw nothing left to
        // let go of, and the session stayed Suppressed for the rest of its life.
        var ct = TestContext.Current.CancellationToken;
        _harness = await MachineHarness.StartAsync();
        var (store, gate) = SuppressionHoldsTests.GatedStore.Over(_harness.Store);
        await using var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        Assert.True(await guard.TickAsync(ct));
        _time.Advance(LoginScreenHeuristic.Suppression + TimeSpan.FromSeconds(1));
        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => guard.TickAsync(ct));
        Assert.True(guard.Holding);

        Assert.False(await guard.TickAsync(ct));

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task AGuardWhosePassThrowsKeepsGuarding()
    {
        // The IndicatorGuard test's twin (T-39). The store refuses the first suppression write, the way a
        // busy disk does; the pass throws, the loop counts it and carries on, and the next pass holds.
        // A loop that ended on the first exception would leave the credential prompt on screen captured
        // for as long as it stayed there (INV-6; the 2026-09-19 fail-open).
        var ct = TestContext.Current.CancellationToken;
        _harness = await MachineHarness.StartAsync();
        var (store, gate) = SuppressionHoldsTests.GatedStore.Over(_harness.Store);
        await using var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        var guard = new SensitiveContextGuard(machine, tick: TimeSpan.FromMilliseconds(5));
        var failures = new List<Type>();
        guard.Failed += ex => failures.Add(ex.GetType());
        using var stop = new CancellationTokenSource();

        guard.Seen(TimeSpan.FromMinutes(1));
        gate.FailNext();
        var running = guard.RunAsync(stop.Token);
        await IndicatorGuardTests.WaitUntilAsync(() => machine.State == SessionState.Suppressed, running);
        stop.Cancel();
        await running;

        Assert.Equal(1, guard.Failures);
        Assert.Equal(typeof(IOException), Assert.Single(failures));
        Assert.True(guard.Holding);
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null));
        return _harness.Machine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
