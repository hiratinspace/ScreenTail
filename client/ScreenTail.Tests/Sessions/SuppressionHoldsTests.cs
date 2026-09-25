using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// One suppressed state, three guards, and whose hold it is (INV-6; 2026-09-20 review).
///
/// The password-field guard, the sensitive-context guard and the indicator guard all suppress the same
/// session, and <c>UnsuppressAsync</c> was unconditional. So the first guard to see its own condition end
/// resumed capture for all of them — including for a guard whose condition was still true, and whose
/// next poll was up to a second away.
///
/// Nothing exotic reaches that. OCR notices a login screen and holds for its window; the technician is
/// still typing in the password field when the window expires; capture resumes with the credential
/// dialog focused, and a click in that second stages a screenshot of it. INV-6 says suppression drops
/// data, and it did — for the guard that had stopped caring.
/// </summary>
public sealed class SuppressionHoldsTests : IAsyncDisposable
{
    private MachineHarness? _harness;

    [Fact]
    public async Task OneGuardLettingGoDoesNotLetGoForAnother()
    {
        // The finding, in three lines. Two conditions are true; one ends.
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await machine.SuppressAsync(CaptureStateReason.SensitiveContext, ct));
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));

        _ = await machine.UnsuppressAsync(CaptureStateReason.SensitiveContext, ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task TheLastGuardToLetGoIsTheOneThatResumesCapture()
    {
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;

        _ = await machine.SuppressAsync(CaptureStateReason.SensitiveContext, ct);
        _ = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct);
        _ = await machine.UnsuppressAsync(CaptureStateReason.SensitiveContext, ct);
        _ = await machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct);

        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task NothingIsStagedWhileAnyGuardIsStillHolding()
    {
        // The claim that matters is about data, not about a state name, so it is read back out of the
        // store the way the other suppression tests do.
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;

        _ = await machine.SuppressAsync(CaptureStateReason.SensitiveContext, ct);
        _ = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct);
        _ = await machine.UnsuppressAsync(CaptureStateReason.SensitiveContext, ct);

        Assert.False(await machine.TryStageFrameAsync(Frame("f-password-dialog"), ct));
        Assert.Equal(0, await _harness!.Store.CountPendingFramesAsync(machine.SessionId!, ct));
    }

    [Fact]
    public async Task AGuardThatNeverHeldCannotResumeSomebodyElsesHold()
    {
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;

        _ = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct);
        _ = await machine.UnsuppressAsync(CaptureStateReason.NoIndicator, ct);

        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task ResumingAPausedSessionDoesNotUndoAGuardsHold()
    {
        // The pipe's Pause and Resume were the other way in: Suppressed -> Paused -> Recording walked
        // straight past a guard that was still holding, which the hotkey path already refused to do.
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;

        _ = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct);
        Assert.True(await machine.PauseAsync(ct));
        Assert.True(await machine.ResumeAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task ASecondSessionDoesNotInheritTheLastOnesHolds()
    {
        // Holds belong to the session, not to the process. One left behind would suppress a session
        // nobody suppressed, for ever.
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;

        _ = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct);
        _ = await machine.DiscardAsync(ct);
        Assert.True(await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: false, policyVersion: null, ct: ct));

        Assert.Equal(SessionState.Recording, machine.State);
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: false, policyVersion: null));
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
