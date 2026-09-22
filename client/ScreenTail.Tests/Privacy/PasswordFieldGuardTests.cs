using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-040 against the real state machine and the real store. The criterion is not "the guard decides to
/// suppress" but "nothing is written while it holds" (INV-6), so these check what ends up in the session,
/// not what the guard thought.
/// </summary>
public sealed class PasswordFieldGuardTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private readonly FakeProbe _probe = new();
    private MachineHarness? _harness;

    [Fact]
    public async Task WhileAPasswordFieldHasFocusNoFrameAndNoTypingIsStored()
    {
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        // Everything the capture loop would try to write during the hold.
        Assert.False(await machine.TryStageFrameAsync(Frame("f1"), TestContext.Current.CancellationToken));
        Assert.False(await machine.TryRecordEventAsync(Typing(12), TestContext.Current.CancellationToken));
        Assert.False(await machine.TryRecordEventAsync(Click(), TestContext.Current.CancellationToken));

        // Counted rather than read back: LoadSessionAsync returns redacted frames only, so a staged frame
        // would not appear there whether or not it was written (INV-1). This asks the store directly.
        Assert.Equal(0, _harness!.Pending.DepthFor(machine.SessionId!));
        var session = (await _harness.Store.LoadSessionAsync(machine.SessionId!))!;
        Assert.DoesNotContain(session.Events, e => e is TypingBurstEvent or ClickEvent);
        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.Equal(1, guard.Holds);
    }

    [Fact]
    public async Task FocusLeavingTheFieldLetsTheNextClickThroughAgain()
    {
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.False(await machine.TryStageFrameAsync(Frame("dropped"), TestContext.Current.CancellationToken));

        _probe.Focused = FocusedField.NotPassword;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
        Assert.True(await machine.TryRecordEventAsync(Click(), TestContext.Current.CancellationToken));
        Assert.True(await machine.TryStageFrameAsync(Frame("kept"), TestContext.Current.CancellationToken));

        // One frame accepted, not two: the one offered during the hold was refused outright, so it
        // never reached the queue either -- and a frame waits in memory now rather than in the store
        // (ADR-0006).
        Assert.Equal(1, _harness!.Pending.DepthFor(machine.SessionId!));
        Assert.True(_harness.Pending.TryTake(out var queued));
        Assert.Equal("kept", queued.Frame.Id);
    }

    [Fact]
    public async Task TheIntervalIsOnTheTimelineWithItsReason()
    {
        // "Intervals audit-logged": the machine writes a capture_state event on every transition, so a hold
        // is the pair — suppressed with reason password_field, then recording — and the timestamps bound it.
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);
        _probe.Focused = FocusedField.NotPassword;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        var session = (await _harness!.Store.LoadSessionAsync(machine.SessionId!))!;
        var states = session.Events.OfType<CaptureStateEvent>().ToList();

        var suppressed = Assert.Single(states, e => e.State == CaptureState.Suppressed);
        Assert.Equal(CaptureStateReason.PasswordField, suppressed.Reason);
        var resumed = states.Last();
        Assert.Equal(CaptureState.Recording, resumed.State);
        Assert.True(resumed.TsMs >= suppressed.TsMs, "the hold has to end after it started");
    }

    [Fact]
    public async Task AnUnknownAnswerKeepsTheLastDecisionRatherThanBecomingANo()
    {
        // Automation fails transiently and on windows we may not inspect. Treating that as "not a password
        // field" would resume capture over a prompt that is still on screen; treating it as one would make
        // capture useless on a machine where automation is broken. It changes nothing, and counts itself.
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        _probe.Focused = FocusedField.Unknown;
        await guard.TickAsync(TestContext.Current.CancellationToken);
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
        Assert.Equal(2, guard.Unknowns);
        Assert.Equal(1, guard.Holds);
    }

    [Fact]
    public async Task AProbeThatThrowsIsAProbeThatDidNotAnswer()
    {
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);
        _probe.Throw = true;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.Equal(1, guard.Unknowns);
    }

    [Fact]
    public async Task TheGuardDoesNotResumeASessionTheTechnicianPaused()
    {
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(await machine.PauseAsync(TestContext.Current.CancellationToken));

        _probe.Focused = FocusedField.NotPassword;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Paused, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task PausingAndResumingOverAPasswordFieldDoesNotLeaveItCapturable()
    {
        // Found in the 2026-09-19 review. "Holding" was the guard's belief and was never checked against
        // the session. Focus a password field: suppressed. Pause and resume: the session is Recording
        // again, the field still has focus, and the guard — believing it was already holding — did
        // nothing until focus moved somewhere else. SensitiveContextGuard had this same bug fixed in it
        // and the fix was never carried across.
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);
        var ct = TestContext.Current.CancellationToken;

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(ct);
        Assert.Equal(SessionState.Suppressed, machine.State);

        Assert.True(await machine.PauseAsync(ct));
        Assert.True(await machine.ResumeAsync(ct));

        // Straight back to suppressed, without waiting for the guard's next tick.
        //
        // This line used to assert Recording, and it was asserting the gap rather than the fix: the
        // 2026-09-19 change made the guard notice on its next poll, which left the password field
        // capturable for up to a second in between. The session now keeps a hold per guard, so resuming
        // cannot walk past one whose condition is still true, and the gap never opens (2026-09-20
        // review).
        Assert.Equal(SessionState.Suppressed, machine.State);

        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
    }

    [Fact]
    public async Task ANewSessionOverTheSamePasswordFieldIsSuppressedToo()
    {
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);
        var ct = TestContext.Current.CancellationToken;

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(ct);
        Assert.True(await machine.DiscardAsync(ct));
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null));

        await guard.TickAsync(ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task APasswordFieldFocusedWhileNotRecordingHoldsNothing()
    {
        var machine = await RecordingAsync();
        Assert.True(await machine.PauseAsync(TestContext.Current.CancellationToken));
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(guard.Holding);
        Assert.Equal(0, guard.Holds);
        Assert.Equal(SessionState.Paused, machine.State);
    }

    [Fact]
    public async Task FocusMovingAroundInsideTheFieldDoesNotStartASecondHold()
    {
        // Focus events arrive for things that are not a change of field — a caret moving, a control
        // re-announcing itself. A hold that stopped and restarted would write a pair of transitions each
        // time and leave a gap in between where a frame could land.
        var machine = await RecordingAsync();
        using var guard = new PasswordFieldGuard(machine, _probe);

        _probe.Focused = FocusedField.Password;
        for (var i = 0; i < 5; i++)
        {
            await guard.TickAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, guard.Holds);
        var session = (await _harness!.Store.LoadSessionAsync(machine.SessionId!))!;
        Assert.Single(session.Events.OfType<CaptureStateEvent>(), e => e.State == CaptureState.Suppressed);
    }

    [Fact]
    public async Task AnIdleServiceDoesNotAskWindowsWhatHasFocus()
    {
        // 2026-09-20 efficiency review. Read() is a cross-process UI Automation call -- ADR-0001 measured
        // it at p95 9.5-21 ms and as much as 106 ms in the worst case -- and the tick made it before
        // looking at the session at all, once a second, all day, on a machine that spends most of its
        // day not recording anything. On its own that is about one to two per cent of a core against
        // ST-031's one per cent idle budget.
        //
        // There is nothing to suppress when no session is running, so there is nothing to ask about.
        _harness = await MachineHarness.StartAsync();
        var probe = new FakeProbe { Focused = FocusedField.Password };
        using var guard = new PasswordFieldGuard(_harness.Machine, probe);

        _ = await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, probe.Reads);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task ARecordingSessionIsStillAsked()
    {
        // The other half: the saving must come out of the idle day, not out of the guard.
        var machine = await RecordingAsync();
        var probe = new FakeProbe { Focused = FocusedField.Password };
        using var guard = new PasswordFieldGuard(machine, probe);

        _ = await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Reads);
        Assert.True(guard.Holding);
    }

    [Fact]
    public async Task AFieldAlreadyFocusedWhenTheSessionStartsIsNoticedWithoutWaiting()
    {
        // The window the early return could have opened. A session that begins over a password field
        // must not wait for the next poll, so the loop is woken by the state change itself.
        //
        // The poll is set far out of reach on purpose: at its usual one second this test would pass
        // whether or not anything woke the loop, and would be evidence of nothing.
        _harness = await MachineHarness.StartAsync();
        var probe = new FakeProbe { Focused = FocusedField.Password };
        using var guard = new PasswordFieldGuard(
            _harness.Machine,
            probe,
            options: new PasswordFieldOptions { PollEvery = TimeSpan.FromMinutes(5) });
        var ct = TestContext.Current.CancellationToken;
        var running = guard.RunAsync(ct);

        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct));

        var suppressed = await WaitFor(() => _harness.Machine.State == SessionState.Suppressed);
        Assert.True(suppressed, "A session that started over a focused password field was not suppressed.");
    }

    /// <summary>Waits for a condition the running loop is expected to bring about, or gives up.</summary>
    private static async Task<bool> WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null));
        return _harness.Machine;
    }

    private static StagedFrame Frame(string id) => new(id, 1_000, FrameTrigger.Click, 100, 100, null, new byte[] { 0xAA });

    private static TypingBurstEvent Typing(int chars) => new() { TsMs = 1_000, CharCount = chars };

    private static ClickEvent Click() => new() { TsMs = 1_000, X = 10, Y = 10, Button = MouseButton.Left };

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private sealed class FakeProbe : IFocusedFieldProbe
    {
        public FocusedField Focused { get; set; } = FocusedField.NotPassword;

        public bool Throw { get; set; }

        /// <summary>How many times the platform was actually asked. The probe is the expensive part.</summary>
        public int Reads { get; private set; }

        public FocusedField Read()
        {
            Reads++;
            return Throw ? throw new InvalidOperationException("automation is unavailable") : Focused;
        }
    }
}
