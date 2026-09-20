using ScreenTail.Core.Capabilities;
using ScreenTail.Core.History;
using ScreenTail.Core.Ipc;
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
internal sealed class CaptureController(
    SessionMachine machine,
    ICapabilityProbe capabilities,
    ISessionStore store,
    Func<DiagnosticsReported> diagnostics,
    Func<CancellationToken, Task<bool>> eraseAll) : IIpcCommandHandler
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
    private ConfirmationIssued? Confirm(RequestConfirmationCommand request) => request.Action switch
    {
        "discard_session" => Issued(DestructiveAction.DiscardSession, "DISCARD"),
        "erase_everything" => Issued(DestructiveAction.EraseEverything, "DELETE EVERYTHING"),

        // An action nobody defined. Answering null is a refusal the caller can see, and inventing a
        // token for it would be a token for something the service cannot name.
        _ => null,
    };

    private ConfirmationIssued Issued(DestructiveAction action, string phrase) =>
        new() { Token = _confirmations.Issue(action), Phrase = phrase };

    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            GetDiagnosticsCommand => diagnostics(),
            ListSessionsCommand list => await ListAsync(list, ct).ConfigureAwait(false),
            RequestConfirmationCommand request => Confirm(request),
            _ => null,
        };
    }

    public async Task<CommandResult> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var accepted = command switch
        {
            // Manual start (Ctrl+Alt+R, tray). ST-023's detector starts sessions with the real tool.
            StartCommand => await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: ct).ConfigureAwait(false),
            PauseCommand => await machine.PauseAsync(ct).ConfigureAwait(false),
            ResumeCommand => await machine.ResumeAsync(ct).ConfigureAwait(false),
            StopCommand => await machine.StopAsync(ct).ConfigureAwait(false),

            // Confirmed first, and spent whether or not the machine accepts it: a token that survived a
            // refused discard would authorise the next one, which is the second round trip gone.
            DiscardCommand discard => _confirmations.Spend(discard.Confirmation, DestructiveAction.DiscardSession)
                && await machine.DiscardAsync(ct).ConfigureAwait(false),

            MarkMomentCommand => await machine.MarkMomentAsync(ct).ConfigureAwait(false),

            // Not while a session is running. Erasing mid-recording destroys work the technician is in
            // the middle of and gives them nothing to look at afterwards; stopping first is one click and
            // makes the decision a decision (2026-09-19 review).
            EraseAllLocalDataCommand erase =>
                machine.State == SessionState.Idle
                && _confirmations.Spend(erase.Confirmation, DestructiveAction.EraseEverything)
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
        var sessions = await store.ListSessionsAsync(ct).ConfigureAwait(false);
        return new SessionsListed
        {
            Sessions = [.. sessions
                .Take(Math.Clamp(command.Limit, 1, 1000))
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
