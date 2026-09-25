using ScreenTail.Core.Capabilities;
using ScreenTail.Core.History;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Review;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Host;

/// <summary>
/// Maps pipe commands onto the session state machine (ST-020), and answers the questions the UI asks
/// about state that only the service can see (ST-085).
///
/// The questions matter as much as the commands. The UI process has no store, no hooks and no counters of
/// its own, and that is deliberate: the service is the only thing that reads a frame, so INV-1's
/// read-path filtering has one owner rather than one per window. Before this, every screen in the UI
/// rendered a literal — a diagnostics panel that told a customer "local-only: yes" from a constant is
/// worse than no panel at all (weaknesses P1-2).
/// </summary>
/// <param name="diagnostics">
/// Built by the host, which is the only thing that can see the redaction worker, the capture loops, the
/// scope decision and the egress guard at once. Counts and states only (INV-10).
/// </param>
/// <param name="eraseAll">
/// Starts "delete everything" (INV-12). Returns once the service has agreed to shut down and erase, which
/// is why the pipe drops immediately afterwards: the store it was serving is about to be deleted.
/// </param>
/// <param name="indicators">
/// What the service knows about whether anything on screen says capture is happening (INV-4). The UI
/// reports it here; <c>IndicatorGuard</c> reads it.
/// </param>
internal sealed class CaptureController(
    SessionMachine machine,
    ICapabilityProbe capabilities,
    ISessionStore store,
    ReviewCommands review,
    PublishCommands publishing,
    DeviceCommands devices,
    Func<DiagnosticsReported> diagnostics,
    Func<CancellationToken, Task<bool>> eraseAll,
    IndicatorReports indicators) : IIpcCommandHandler
{
    public CaptureStateSnapshot CurrentState => machine.Snapshot;

    // Probed on each request rather than cached: a technician can revoke microphone access mid-session.
    public CapabilitiesReported CurrentCapabilities => capabilities.Probe().ToWire();

    private readonly Confirmations _confirmations = new();

    /// <summary>
    /// Issues a token for something irreversible, and says what the technician has to type.
    ///
    /// The phrase comes from here rather than from the UI so that the wording a customer is shown and
    /// the wording the service expects cannot drift apart. Spec §3 asks for a typed confirmation because
    /// agreeing should take an act rather than a reflex.
    /// </summary>
    private ConfirmationIssued? Confirm(RequestConfirmationCommand request, Guid caller) => request.Action switch
    {
        "discard_session" => Issued(DestructiveAction.DiscardSession, "DISCARD", caller),
        "erase_everything" => Issued(DestructiveAction.EraseEverything, "DELETE EVERYTHING", caller),

        // An action nobody defined. Answering null is a refusal the caller can see, and inventing a
        // token for it would be a token for something the service cannot name.
        _ => null,
    };

    /// <summary>Records that this window says it is showing the indicator. Always accepted.</summary>
    private bool Report(Guid caller)
    {
        indicators.Report(caller);
        return true;
    }

    private ConfirmationIssued Issued(DestructiveAction action, string phrase, Guid caller) =>
        new() { Token = _confirmations.Issue(action, caller), Phrase = phrase };

    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            GetDiagnosticsCommand => diagnostics(),
            ListSessionsCommand list => await ListAsync(list, ct).ConfigureAwait(false),
            RequestConfirmationCommand request => Confirm(request, caller),

            // Review's questions: the session, a frame, a blur (ST-085 remainder); and publishing's
            // (ST-093): what is connected, a ticket search, the publish itself.
            _ => await review.ReplyToAsync(command, ct).ConfigureAwait(false)
                ?? await publishing.ReplyToAsync(command, ct).ConfigureAwait(false)
                ?? await devices.ReplyToAsync(command, ct).ConfigureAwait(false),
        };
    }

    public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Review's edits, and its reasons when a question had no answer. Chained first because these are
        // about a stored session, not the machine's state, and the message below would be wrong for them.
        if (await review.HandleAsync(command, ct).ConfigureAwait(false) is { } reviewed)
        {
            return reviewed;
        }

        if (await publishing.HandleAsync(command, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if (await devices.HandleAsync(command, ct).ConfigureAwait(false) is { } notActivated)
        {
            return notActivated;
        }

        var accepted = command switch
        {
            // Manual start (Ctrl+Alt+R, tray). ST-023's detector starts sessions with the real tool.
            StartCommand => await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: ct).ConfigureAwait(false),
            PauseCommand => await machine.PauseAsync(ct).ConfigureAwait(false),
            ResumeCommand => await machine.ResumeAsync(ct).ConfigureAwait(false),
            StopCommand => await machine.StopAsync(ct).ConfigureAwait(false),

            // Confirmed first, and spent whether or not the machine accepts it: a token that survived a
            // refused discard would authorise the next one, which is the second round trip gone.
            //
            // Spent by the connection that asked for it. Without that the token was a bearer token and
            // any authenticated client could spend one the technician had just been handed.
            DiscardCommand discard => _confirmations.Spend(discard.Confirmation, DestructiveAction.DiscardSession, caller)
                && await machine.DiscardAsync(ct).ConfigureAwait(false),

            MarkMomentCommand => await machine.MarkMomentAsync(ct).ConfigureAwait(false),

            // The UI saying the pill is on screen. Accepted from whichever connection sent it and
            // expiring on its own, so a UI that stops painting stops counting (INV-4).
            IndicatorShowingCommand => Report(caller),

            // Not while a session is running. Erasing mid-recording destroys work the technician is in
            // the middle of and gives them nothing to look at afterwards; stopping first is one click and
            // makes the decision a decision (2026-09-19 review).
            //
            // "Running", not "Idle". The machine does not return to Idle after a session -- it rests in
            // draft_ready or draft_failed until something discards or the service restarts, and with
            // today's stub sender every session ends in draft_failed. So the Idle test took INV-12's
            // "delete everything" away from a technician the moment they recorded anything at all, which
            // is a guard that destroyed the thing it was guarding (2026-09-20 review).
            EraseAllLocalDataCommand erase =>
                machine.State is not (SessionState.Recording or SessionState.Paused
                    or SessionState.Suppressed or SessionState.Finalizing)
                && _confirmations.Spend(erase.Confirmation, DestructiveAction.EraseEverything, caller)
                && await eraseAll(ct).ConfigureAwait(false),
            _ => false,
        };

        return new CommandResult
        {
            RequestId = command.RequestId,
            Ok = accepted,
            Error = accepted ? null : $"Not possible while {machine.State.ToWire()}.",
        };
    }

    private async Task<SessionsListed> ListAsync(ListSessionsCommand command, CancellationToken ct)
    {
        // The limit reaches the query rather than being applied to everything it read.
        var limit = Math.Clamp(command.Limit, 1, 1000);
        var sessions = await store.ListSessionsAsync(limit, ct).ConfigureAwait(false);
        return new SessionsListed
        {
            Sessions = [.. sessions
                .Select(s => new SessionRow
                {
                    Id = s.Id,
                    StartedAt = s.StartedAt,
                    DurationMs = s.DurationMs ?? 0,

                    // ST-079's rule, applied here rather than in the UI, so the draft's text never
                    // crosses the pipe just to be turned back into the word "Draft".
                    Status = SessionHistory.StatusOf(s).ToString().ToLowerInvariant(),
                    Tool = s.RemoteTool,

                    // Redacted frames only. The store already counts them that way; saying so here keeps
                    // the reason with the number (INV-1).
                    Frames = s.Frames,
                    FramesPurged = s.FramesPurgedUnredacted,
                })],
        };
    }
}
