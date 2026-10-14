using System.Reflection;
using ScreenTail.Core.Audit;
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
    private SessionMachine? _gated;

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

    [Fact]
    public async Task AResumeDecidesFromTheHoldsThatAreLeftWhenItsTurnComes()
    {
        // 2026-10-12 review, alongside T-41. Resume read the holds before it waited for its turn, so a
        // guard letting go of its hold in between left Resume going back to Suppressed with nothing
        // held, and nothing would ever lift that. The pause is held inside its store write so the order
        // is the one that loses: pause, then the guard letting go, then the resume.
        var ct = TestContext.Current.CancellationToken;
        _harness = await MachineHarness.StartAsync();
        var (store, gate) = GatedStore.Over(_harness.Store);
        var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        await using var _ = machine;
        Assert.True(await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: false, policyVersion: null, ct: ct));
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));

        gate.Close();
        var pause = machine.PauseAsync(ct);
        await gate.Entered.Task.WaitAsync(ct);
        var letGo = machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct);
        var resume = machine.ResumeAsync(ct);
        gate.Open();

        // The losing order relies on SemaphoreSlim handing the lock to queued async waiters first come,
        // first served. That is how it behaves but not something it documents, so nothing here depends
        // on the order for passing: whichever of the two goes first, the session has to end up Recording
        // with nothing held. Only the order that used to lose can show the old bug.
        Assert.True(await pause);
        await letGo;
        Assert.True(await resume);
        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(machine.IsHeld(CaptureStateReason.PasswordField));
    }

    [Fact]
    public async Task AHoldTakenDuringAPauseIsWhereResumeGoes()
    {
        // 2026-10-12 review, round 2. A condition that began, or came back, while the session was paused
        // recorded no hold, so Resume went to Recording over it until the guard's next tick: one probe
        // for a password field, up to 500 ms with no indicator on screen (INV-4, INV-6). The hold is
        // recorded now, the session stays paused, and Resume goes straight to Suppressed.
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await machine.PauseAsync(ct));

        Assert.True(await machine.SuppressAsync(CaptureStateReason.NoIndicator, ct));
        Assert.Equal(SessionState.Paused, machine.State);
        Assert.True(machine.IsHeld(CaptureStateReason.NoIndicator));

        Assert.True(await machine.ResumeAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.False(await machine.TryStageFrameAsync(Frame("f-no-indicator"), ct));
        var stored = (await _harness!.Store.LoadSessionAsync(machine.SessionId!, ct))!;
        var last = stored.Events.OfType<CaptureStateEvent>().Last();
        Assert.Equal(CaptureState.Suppressed, last.State);
        Assert.Equal(CaptureStateReason.NoIndicator, last.Reason);
    }

    [Fact]
    public async Task NoSessionMeansNoHold()
    {
        // The other edge of the same rule. With nothing running there is nothing to hold down, and a
        // hold recorded here would suppress the next session, which nobody suppressed.
        _harness = await MachineHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await _harness.Machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.False(_harness.Machine.IsHeld(CaptureStateReason.PasswordField));

        Assert.True(await _harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: false, policyVersion: null, ct: ct));
        Assert.Equal(SessionState.Recording, _harness.Machine.State);
    }

    [Fact]
    public async Task AStopThatWasNotSavedKeepsTheHoldsItHad()
    {
        // The holds were cleared before the move to Finalizing was written. When that write failed, the
        // session stayed Suppressed with nothing holding it: a guard whose condition had ended saw nothing
        // to let go of, and capture stayed off until Stop was tried again.
        var ct = TestContext.Current.CancellationToken;
        var (machine, gate) = await RecordingOverGateAsync(ct);
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));

        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => machine.StopAsync(ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(machine.IsHeld(CaptureStateReason.PasswordField));
        Assert.True(await machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task ASuppressionThatWasNotSavedAddsNoActiveTime()
    {
        // The active-time accounting moved before the state write. A write that failed left the session
        // Recording with the time since it began counted twice, and every retry counted it again.
        var clock = new ManualTime(DateTimeOffset.UnixEpoch);
        var ct = TestContext.Current.CancellationToken;
        var (machine, gate) = await RecordingOverGateAsync(ct, clock);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.Equal(150, machine.Snapshot.ElapsedMs!.Value);
    }

    [Fact]
    public async Task ASuppressedIntervalIsRecordedAtItsRealLengthWhenALetGoHadToBeTriedAgain()
    {
        // The interval's audit row was written, and its start forgotten, before the state write. When
        // that write failed and the guard tried again, the row already said the hold had ended, and the
        // real end was never recorded.
        var clock = new ManualTime(DateTimeOffset.UnixEpoch);
        var ct = TestContext.Current.CancellationToken;
        var (machine, gate) = await RecordingOverGateAsync(ct, clock);
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));

        clock.Advance(TimeSpan.FromMilliseconds(1_000));
        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(await machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct));

        var rows = await _harness!.Store.GetAuditAsync(machine.SessionId!, ct);
        var interval = Assert.Single(rows, r => r.Type == AuditTypes.CaptureSuppressed);
        Assert.Equal(1_500, interval.Count);
    }

    [Fact]
    public async Task ASuppressionThatWasNotSavedHoldsNothing()
    {
        // 2026-10-12 review, round 2. The hold went into the set before the state write. When that write
        // failed, the session stayed Recording with the hold still in the set, so a guard asking IsHeld
        // was told it held capture down and never asked again. Capture ran on over the very thing the
        // guard had seen.
        var ct = TestContext.Current.CancellationToken;
        var (machine, gate) = await RecordingOverGateAsync(ct);

        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => machine.SuppressAsync(CaptureStateReason.PasswordField, ct));

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(machine.IsHeld(CaptureStateReason.PasswordField));

        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(machine.IsHeld(CaptureStateReason.PasswordField));
    }

    [Fact]
    public async Task ALetGoThatWasNotSavedKeepsItsHold()
    {
        // The mirror. The hold left the set before the state write; when that failed the session stayed
        // Suppressed with nothing holding it, the guard saw nothing left to let go of, and nothing would
        // ever lift the suppression.
        var ct = TestContext.Current.CancellationToken;
        var (machine, gate) = await RecordingOverGateAsync(ct);
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));

        gate.FailNext();
        _ = await Assert.ThrowsAsync<IOException>(() => machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct));

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(machine.IsHeld(CaptureStateReason.PasswordField));

        Assert.True(await machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(machine.IsHeld(CaptureStateReason.PasswordField));
    }

    [Fact]
    public async Task ATransitionThatHappenedKeepsItsHoldEvenWhenSomethingAfterItThrows()
    {
        // The undo is for a transition that did not happen. A listener throwing after the state has
        // moved is not that: capture is off, so the hold stays, and undoing it would leave the session
        // Suppressed with nothing holding it. The same goes for a release.
        var machine = await RecordingAsync();
        var ct = TestContext.Current.CancellationToken;
        var throwOnce = true;
        machine.StateChanged += _ =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("a listener failed");
            }
        };

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(machine.IsHeld(CaptureStateReason.PasswordField));

        throwOnce = true;
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(machine.IsHeld(CaptureStateReason.PasswordField));
    }

    [Fact]
    public async Task ASecondHoldThatNeedsNoWriteIsKeptWhenAnotherIsAlreadyInPlace()
    {
        // A second guard joining a suppression writes nothing, so there is nothing to fail and nothing to
        // undo: its hold stays, and the first guard letting go leaves capture off.
        var ct = TestContext.Current.CancellationToken;
        var (machine, gate) = await RecordingOverGateAsync(ct);
        Assert.True(await machine.SuppressAsync(CaptureStateReason.SensitiveContext, ct));

        gate.FailNext();
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        gate.Disarm();

        Assert.True(machine.IsHeld(CaptureStateReason.PasswordField));
        _ = await machine.UnsuppressAsync(CaptureStateReason.SensitiveContext, ct);
        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    private async Task<(SessionMachine Machine, GatedStore Gate)> RecordingOverGateAsync(CancellationToken ct, TimeProvider? time = null)
    {
        _harness = await MachineHarness.StartAsync();
        var (store, gate) = GatedStore.Over(_harness.Store);
        var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter(), time);
        _gated = machine;
        Assert.True(await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: false, policyVersion: null, ct: ct));
        return (machine, gate);
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
        if (_gated is not null)
        {
            await _gated.DisposeAsync();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// The real store, with one state write that can be made to wait or to fail. Holding a transition
    /// inside its store write is how a test lines up the calls queued behind it in a known order; failing
    /// one is how it reaches a transition that did not happen. Not sealed, because DispatchProxy derives
    /// from it.
    /// </summary>
    public class GatedStore : DispatchProxy
    {
        private ISessionStore? _inner;
        private TaskCompletionSource? _gate;
        private bool _armed;
        private bool _failNext;

        /// <summary>Completed once a state write has reached the closed gate.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The store's two faces at once, so the machine still finds the audit log behind the proxy and
        /// a test can read the suppressed intervals it records.
        /// </summary>
        public interface IAuditedStore : ISessionStore, IAuditLog
        {
        }

        public static (ISessionStore Store, GatedStore Gate) Over(SqliteSessionStore inner)
        {
            var store = Create<IAuditedStore, GatedStore>();
            var gate = (GatedStore)(object)store;
            gate._inner = inner;
            return (store, gate);
        }

        /// <summary>The next state write waits until <see cref="Open"/>.</summary>
        public void Close()
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _armed = true;
        }

        public void Open() => _gate?.TrySetResult();

        /// <summary>The next state write fails, the way a busy or full disk would, and later ones go through.</summary>
        public void FailNext() => _failNext = true;

        /// <summary>Withdraws a <see cref="FailNext"/> that nothing used.</summary>
        public void Disarm() => _failNext = false;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ISessionStore.SetSessionStateAsync) && _failNext)
            {
                _failNext = false;
                return Task.FromException(new IOException("The store could not be written."));
            }

            if (targetMethod.Name == nameof(ISessionStore.SetSessionStateAsync) && _armed && _gate is { } gate)
            {
                _armed = false;
                _ = Entered.TrySetResult();
                return WriteWhenOpenAsync(gate.Task, targetMethod, args);
            }

            return targetMethod.Invoke(_inner, args);
        }

        private async Task WriteWhenOpenAsync(Task gate, MethodInfo write, object?[]? args)
        {
            await gate.ConfigureAwait(false);
            await ((Task)write.Invoke(_inner, args)!).ConfigureAwait(false);
        }
    }
}
