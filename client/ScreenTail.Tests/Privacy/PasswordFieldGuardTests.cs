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
        Assert.Equal(0, await _harness!.Store.CountPendingFramesAsync(machine.SessionId!, TestContext.Current.CancellationToken));
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

        // One frame staged, not two: the one offered during the hold never reached the store.
        Assert.Equal(1, await _harness!.Store.CountPendingFramesAsync(machine.SessionId!, TestContext.Current.CancellationToken));
        var pending = await _harness.Store.TakeNextPendingFrameAsync(TestContext.Current.CancellationToken);
        Assert.Equal("kept", pending!.Id);
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

        public FocusedField Read() => Throw ? throw new InvalidOperationException("automation is unavailable") : Focused;
    }
}
