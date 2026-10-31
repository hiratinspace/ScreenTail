using ScreenTail.Core.Review;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>
/// Review's Discard and History's bulk discard from the UI's side (Spec §5 S3, S4; contract v6).
///
/// The same two round trips as the tray's discard (<see cref="ConfirmedAction"/>), with the sessions
/// named on both: the token the service issues is good for exactly those ids, and the command carries
/// the same ids back. Afterwards the shell is told, so a discarded session is not left open in Review,
/// and Review goes to History.
///
/// Neither asks anything while the session is still being captured: Spec §4 allows no modal during a
/// recording, and Review of the running session is read-only until it stops (§5 S2). The screen says why
/// beside the button instead, which a keyboard user can reach. The service would stop a recording session
/// before discarding it all the same (v0.4.1 Q3); this keeps the dialog off the screen.
///
/// Platform-neutral, so the flow is tested on the Mac over a real pipe (ADR-0002).
/// </summary>
/// <param name="state">The store the shell binds to: what capture is doing, and which session Review shows.</param>
/// <param name="request">Asks the service a question whose answer is an event; null when there is nobody to ask.</param>
/// <param name="send">Sends a command and brings back the service's result.</param>
public sealed class SessionDiscard(
    ShellState state,
    Func<Func<int, IpcCommand>, CancellationToken, Task<ConfirmationIssued?>> request,
    Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> send)
{
    public const string StillCapturing = "This session is still being captured. Stop it first, then discard it.";

    public const string OneStillCapturing = "One of these sessions is still being captured. Stop it first, then discard them.";

    private readonly ShellState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly Func<Func<int, IpcCommand>, CancellationToken, Task<ConfirmationIssued?>> _request = request ?? throw new ArgumentNullException(nameof(request));
    private readonly Func<Func<int, IpcCommand>, CancellationToken, Task<CommandResult>> _send = send ?? throw new ArgumentNullException(nameof(send));

    /// <summary>Over the shell's connection, as the running application has it.</summary>
    public SessionDiscard(ShellState state, CaptureConnection connection)
        : this(
            state,
            (build, ct) => (connection ?? throw new ArgumentNullException(nameof(connection))).RequestAsync<ConfirmationIssued>(build, ct),
            (build, ct) => connection.SendAsync(build, ct))
    {
    }

    /// <summary>
    /// Why these sessions cannot be discarded right now, or null when they can be asked about.
    ///
    /// Not connected counts: the last thing the service said may be that it was recording, and nothing
    /// may be assumed about a session it cannot be asked about.
    /// </summary>
    public string? BlockedBecause(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (_state.Snapshot.KnownCapture is not { } capture)
        {
            return ConfirmedAction.NotConnected;
        }

        var capturing = capture.State is CaptureStates.Recording or CaptureStates.Paused or CaptureStates.Suppressed or CaptureStates.Finalizing;
        return capturing && capture.SessionId is { } live && sessionIds.Contains(live, StringComparer.Ordinal)
            ? (sessionIds.Count == 1 ? StillCapturing : OneStillCapturing)
            : null;
    }

    /// <summary>
    /// Review's Discard, once the technician has typed DISCARD in the dialog. A token is asked for now,
    /// fresh, as <see cref="ConfirmedAction"/> asks for its second: a service that asks for anything but
    /// DISCARD is not one the technician agreed with, and nothing is sent.
    /// </summary>
    public async Task<ConfirmedResult> FromReviewAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        IReadOnlyList<string> ids = [sessionId];
        if (BlockedBecause(ids) is { } blocked)
        {
            return Blocked(blocked);
        }

        var issued = await _request(Ask(History.DiscardCommands.OneSession, ids), ct).ConfigureAwait(false);
        if (issued is null)
        {
            return new ConfirmedResult(ConfirmedOutcome.NotAvailable, ConfirmedAction.NotConnected);
        }

        if (!string.Equals(issued.Phrase, TypedConfirmation.DiscardWord, StringComparison.Ordinal))
        {
            return new ConfirmedResult(ConfirmedOutcome.Refused, ConfirmedAction.PhraseChanged);
        }

        var result = await _send(Discard(ids, issued.Token), ct).ConfigureAwait(false);
        return Settled(result.Ok ? new ConfirmedResult(ConfirmedOutcome.Done) : Refused(result), ids, showHistory: true);
    }

    /// <summary>
    /// History's bulk discard: the service says what to type, which is how many sessions it bound the
    /// token to (v0.4.1 Q6), the technician types it in <paramref name="typed"/>, and the command goes
    /// back with a fresh token for the same ids.
    /// </summary>
    public async Task<ConfirmedResult> BulkAsync(IReadOnlyList<string> sessionIds, Func<string, Task<bool>> typed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentNullException.ThrowIfNull(typed);
        if (BlockedBecause(sessionIds) is { } blocked)
        {
            return Blocked(blocked);
        }

        var result = await ConfirmedAction.RunAsync(
            (action, c) => _request(Ask(action, sessionIds), c),
            History.DiscardCommands.Sessions,
            typed,
            token => Discard(sessionIds, token),
            _send,
            ct).ConfigureAwait(false);
        return Settled(result, sessionIds, showHistory: false);
    }

    private ConfirmedResult Settled(ConfirmedResult result, IReadOnlyList<string> ids, bool showHistory)
    {
        if (result.Done)
        {
            _state.Discarded(ids, showHistory);
        }

        return result;
    }

    private static ConfirmedResult Blocked(string reason) =>
        reason == ConfirmedAction.NotConnected
            ? new ConfirmedResult(ConfirmedOutcome.NotAvailable, reason)
            : new ConfirmedResult(ConfirmedOutcome.Refused, reason);

    private static ConfirmedResult Refused(CommandResult result) =>
        new(ConfirmedOutcome.Refused, string.IsNullOrWhiteSpace(result.Error) ? ConfirmedAction.RefusedWithoutReason : result.Error);

    private static Func<int, IpcCommand> Ask(string action, IReadOnlyList<string> ids) =>
        id => new RequestConfirmationCommand { RequestId = id, Action = action, SessionIds = ids };

    private static Func<int, IpcCommand> Discard(IReadOnlyList<string> ids, string token) =>
        id => new DiscardSessionsCommand { RequestId = id, SessionIds = ids, Confirmation = token };
}
