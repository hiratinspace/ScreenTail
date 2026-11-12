using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>
/// Settings → Privacy's "Delete everything" from the UI's side (Spec §5 S6, INV-12): the
/// <see cref="ConfirmedAction"/> flow for <c>erase_everything</c>, with nothing asked while a session is
/// being captured.
///
/// Its typed dialog is a modal, and Spec §4 allows none during a recording, so the technician is told why
/// beside the button instead, as History's bulk discard and the mark do. The state is read again once the
/// dialog is answered: the service starts sessions by itself, and one may have begun, or the service
/// gone, while the phrase was typed. Nothing more is asked or sent then. The service refuses an erase
/// while a session runs all the same; this keeps the dialog, and the message box that showed its
/// refusal, off the screen.
///
/// Platform-neutral, so the flow is tested on the Mac (ADR-0002); the shell supplies the dialog.
/// </summary>
/// <param name="state">The store the shell binds to: what capture is doing.</param>
/// <param name="request">Asks the service a question whose answer is an event; null when there is nobody to ask.</param>
/// <param name="send">Sends a command and brings back the service's result.</param>
public sealed class DeleteEverything(
    ShellState state,
    Func<Func<int, IpcCommand>, CancellationToken, Task<ConfirmationIssued?>> request,
    Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> send)
{
    /// <summary>The action the service issues the token for.</summary>
    public const string Action = "erase_everything";

    public const string WhileCapturing = "A session is being captured. Stop it first, then delete everything.";

    private readonly ShellState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly Func<Func<int, IpcCommand>, CancellationToken, Task<ConfirmationIssued?>> _request = request ?? throw new ArgumentNullException(nameof(request));
    private readonly Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> _send = send ?? throw new ArgumentNullException(nameof(send));

    /// <summary>Over the shell's connection, as the running application has it.</summary>
    public DeleteEverything(ShellState state, CaptureConnection connection)
        : this(
            state,
            (build, ct) => (connection ?? throw new ArgumentNullException(nameof(connection))).RequestAsync<ConfirmationIssued>(build, ct),
            (build, ct) => connection.SendAsync(build, ct))
    {
    }

    /// <summary>
    /// Why the technician cannot be asked right now, or null when they can. Not connected counts: the
    /// last thing the service said may be that it was recording.
    /// </summary>
    public string? BlockedBecause() =>
        _state.Snapshot.KnownCapture is not { } capture
            ? ConfirmedAction.NotConnected
            : SessionDiscard.Capturing(capture) ? WhileCapturing : null;

    /// <param name="typed">Shows the phrase the service asks for and says whether the technician typed it.</param>
    public async Task<ConfirmedResult> RunAsync(Func<string, Task<bool>> typed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(typed);
        if (BlockedBecause() is { } blocked)
        {
            return Blocked(blocked);
        }

        string? since = null;
        var result = await ConfirmedAction.RunAsync(
            (action, c) => _request(id => new RequestConfirmationCommand { RequestId = id, Action = action }, c),
            Action,
            async phrase =>
            {
                if (!await typed(phrase).ConfigureAwait(false))
                {
                    return false;
                }

                since = BlockedBecause();
                return since is null;
            },
            token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            _send,
            ct).ConfigureAwait(false);
        return since is not null ? Blocked(since) : result;
    }

    private static ConfirmedResult Blocked(string reason) =>
        new(reason == ConfirmedAction.NotConnected ? ConfirmedOutcome.NotAvailable : ConfirmedOutcome.Refused, reason);
}
