using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>
/// History's "Mark as published" from the UI's side (contract v10): a publish of the session may have
/// reached its ticket, the technician looked, the note is there, and they settle it without sending
/// anything again.
///
/// A statement about the ticket, which ScreenTail cannot check, so the technician is asked before it is
/// sent. Not a typed confirmation and no token: nothing is deleted and nothing leaves the machine. The
/// service decides whether the session is one this may be said about, and its words are shown when it
/// is not.
///
/// Nothing is asked while a session is being captured: Spec §4 allows no modal during a recording, and
/// the screen says why beside the button instead.
///
/// Platform-neutral, so the flow is tested on the Mac (ADR-0002); the shell supplies the dialog.
/// </summary>
/// <param name="state">The store the shell binds to: what capture is doing.</param>
/// <param name="send">Sends a command and brings back the service's result.</param>
public sealed class MarkAsPublished(
    ShellState state,
    Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> send)
{
    /// <summary>The button, the dialog's title and the confirming answer.</summary>
    public const string Action = "Mark as published";

    /// <summary>What the dialog asks. What is being claimed, and what follows from it.</summary>
    public const string Question =
        "Mark this session as published? Only do this after checking the ticket."
        + "\n\n"
        + "ScreenTail will record that what the earlier publish sent is on the ticket, and will not send it again. "
        + "A published session is kept and can no longer be discarded.";

    public const string WhileCapturing = "A session is being captured. Stop it first, then mark this one as published.";

    /// <summary>Said beside the button once the service has done it.</summary>
    public const string Done = "Marked as published.";

    private readonly ShellState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> _send = send ?? throw new ArgumentNullException(nameof(send));

    /// <summary>Over the shell's connection, as the running application has it.</summary>
    public MarkAsPublished(ShellState state, CaptureConnection connection)
        : this(state, (build, ct) => (connection ?? throw new ArgumentNullException(nameof(connection))).SendAsync(build, ct))
    {
    }

    /// <summary>
    /// Why the technician cannot be asked right now, or null when they can. Not connected counts: the
    /// last thing the service said may be that it was recording.
    /// </summary>
    public string? BlockedBecause()
    {
        if (_state.Snapshot.KnownCapture is not { } capture)
        {
            return ConfirmedAction.NotConnected;
        }

        return capture.State is CaptureStates.Recording or CaptureStates.Paused or CaptureStates.Suppressed or CaptureStates.Finalizing
            ? WhileCapturing
            : null;
    }

    /// <param name="confirmed">Shows <see cref="Question"/> and says whether the technician agreed.</param>
    public async Task<ConfirmedResult> RunAsync(string sessionId, Func<Task<bool>> confirmed, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(confirmed);
        if (BlockedBecause() is { } blocked)
        {
            return new ConfirmedResult(blocked == ConfirmedAction.NotConnected ? ConfirmedOutcome.NotAvailable : ConfirmedOutcome.Refused, blocked);
        }

        if (!await confirmed().ConfigureAwait(false))
        {
            return new ConfirmedResult(ConfirmedOutcome.Declined);
        }

        var result = await _send(id => new MarkPublishedCommand { RequestId = id, SessionId = sessionId }, ct).ConfigureAwait(false);
        return result.Ok
            ? new ConfirmedResult(ConfirmedOutcome.Done)
            : new ConfirmedResult(ConfirmedOutcome.Refused, string.IsNullOrWhiteSpace(result.Error) ? ConfirmedAction.RefusedWithoutReason : result.Error);
    }
}
