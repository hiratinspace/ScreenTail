using ScreenTail.Core.Ipc;
using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Ipc;

/// <summary>
/// What the "What's being captured right now?" panel is told, from the things only the service can see
/// (ST-071, ST-085; 2026-10-12 review, S-129 and T-113).
///
/// The host used to hard-code two of the fields: suppression was always null and the redaction backlog
/// was always 0, so the panel said "suppressed: no" over a password field and "queue 0" with frames
/// waiting. The mapping lives in Core now so the Mac suite can hold every field to its source.
/// </summary>
public sealed class ServiceDiagnosticsTests
{
    [Fact]
    public void TheDiagnosticsPanelReportsTheLiveCounts()
    {
        var state = new CaptureStateSnapshot { State = CaptureStates.Suppressed, SessionId = "s1", PendingRedactions = 4 };
        var redaction = new RedactionProgress(Frames: 40, Unreadable: 1, Unread: 2, Masked: new Dictionary<MaskKind, long>());

        var reported = ServiceDiagnostics.Report(
            serviceVersion: "0.1.0",
            scope: "Capturing: ScreenConnect",
            microphone: "Headset Microphone (Realtek)",
            state,
            isHeld: reason => reason is CaptureStateReason.PasswordField,
            redactionBacklog: 4,
            redaction,
            framesRefused: 0,
            keystrokesDropped: 17,
            egressBlocked: 5,
            localOnly: true,
            policyVersion: "v3",
            workingSetBytes: 1024);

        Assert.Equal("password_field", reported.Suppression);
        Assert.Equal(4, reported.RedactionBacklog);
        Assert.Equal(3, reported.FramesDropped);
        Assert.Equal(17, reported.KeystrokesDropped);
        Assert.Equal(5, reported.EgressBlocked);
        Assert.True(reported.LocalOnly);
        Assert.Equal("v3", reported.PolicyVersion);
        Assert.Equal("Capturing: ScreenConnect", reported.Scope);
        Assert.Equal("Headset Microphone (Realtek)", reported.Microphone);
        Assert.Equal(1024, reported.WorkingSetBytes);
        Assert.Equal("0.1.0", reported.ServiceVersion);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Idle)]
    public void NothingIsSuppressedUnlessTheStateSaysSo(string state)
    {
        // A pause keeps its holds (T-41), so a held reason on its own is not a suppression; the machine
        // being in Suppressed is. The panel must agree with the HUD about which it is.
        var snapshot = new CaptureStateSnapshot { State = state, PendingRedactions = 0 };

        Assert.Null(ServiceDiagnostics.Suppression(snapshot, reason => reason is CaptureStateReason.PasswordField));
    }

    [Fact]
    public void EveryHoldIsNamedInItsWireForm()
    {
        // The same names the state events and the contract use (password_field, not PasswordField), so
        // the clipboard text and the audit export say the same thing about the same moment.
        var snapshot = new CaptureStateSnapshot { State = CaptureStates.Suppressed, PendingRedactions = 0 };

        var suppression = ServiceDiagnostics.Suppression(
            snapshot,
            reason => reason is CaptureStateReason.PasswordField or CaptureStateReason.SensitiveContext);

        Assert.Equal("password_field, sensitive_context", suppression);
    }

    [Fact]
    public void SuppressedWithNoNamedHoldStillSaysSuppressed()
    {
        // Fail towards saying so: a Suppressed state whose hold the panel cannot name is still a
        // suppression, not "no".
        var snapshot = new CaptureStateSnapshot { State = CaptureStates.Suppressed, PendingRedactions = 0 };

        Assert.Equal(CaptureStates.Suppressed, ServiceDiagnostics.Suppression(snapshot, _ => false));
    }

    [Fact]
    public void AFrameTheQueueRefusedIsADroppedFrame()
    {
        // A frame that found the queue of frames waiting to be read full was refused and is gone, as
        // surely as one the worker could not read (ADR-0006, INV-1). The machine counts it against the
        // session, and the panel's "frames dropped" said 0 because it only added up what the worker saw.
        var redaction = new RedactionProgress(Frames: 40, Unreadable: 1, Unread: 2, Masked: new Dictionary<MaskKind, long>());

        var reported = ServiceDiagnostics.Report(
            serviceVersion: "0.1.0",
            scope: null,
            microphone: null,
            new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", PendingRedactions = 0 },
            isHeld: _ => false,
            redactionBacklog: 0,
            redaction,
            framesRefused: 5,
            keystrokesDropped: null,
            egressBlocked: 0,
            localOnly: false,
            policyVersion: "default",
            workingSetBytes: 0);

        Assert.Equal(8, reported.FramesDropped);
    }

    [Fact]
    public void ScreenshotsRefusedForADiscardedSessionAreCountedApart()
    {
        // Second review of #243. A frame the store refused because its session was discarded was read and
        // masked, so it is not one lost unchecked (INV-1), and it was not stored, so it is not redacted
        // either. It has its own number, and nothing else about it.
        var redaction = new RedactionProgress(Frames: 40, Unreadable: 0, Unread: 0, Masked: new Dictionary<MaskKind, long>(), Refused: 3);

        var reported = ServiceDiagnostics.Report(
            serviceVersion: "0.1.0",
            scope: null,
            microphone: null,
            new CaptureStateSnapshot { State = CaptureStates.Idle, PendingRedactions = 0 },
            isHeld: _ => false,
            redactionBacklog: 0,
            redaction,
            framesRefused: 0,
            keystrokesDropped: null,
            egressBlocked: 0,
            localOnly: false,
            policyVersion: "default",
            workingSetBytes: 0);

        Assert.Equal(3, reported.FramesAfterDiscard);
        Assert.Equal(0, reported.FramesDropped);
    }

    [Fact]
    public void BeforeAnythingIsWiredTheCountsAreZeroAndNothingIsInFront()
    {
        // The report the controller can be asked for before the capture loops exist. Zeros are honest
        // there: nothing has run, so nothing has been dropped or refused.
        var reported = ServiceDiagnostics.Report(
            serviceVersion: "0.1.0",
            scope: null,
            microphone: null,
            new CaptureStateSnapshot { State = CaptureStates.Idle, PendingRedactions = 0 },
            isHeld: _ => false,
            redactionBacklog: 0,
            redaction: null,
            framesRefused: 0,
            keystrokesDropped: null,
            egressBlocked: 0,
            localOnly: false,
            policyVersion: "default",
            workingSetBytes: 0);

        Assert.Equal(ServiceDiagnostics.NothingInFront, reported.Scope);
        Assert.Null(reported.Suppression);
        Assert.Null(reported.Microphone);
        Assert.Equal(0, reported.RedactionBacklog);
        Assert.Equal(0, reported.FramesDropped);
        Assert.Equal(0, reported.KeystrokesDropped);
    }
}
