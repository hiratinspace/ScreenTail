using System.Globalization;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Review;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.History;

/// <summary>
/// The service's side of Review's Discard and History's bulk discard (Spec §5 S3, S4; contract v6).
///
/// The tray's discard names nothing and acts on the session the machine is resting on, and it stays the
/// controller's, unchanged. These name their sessions, because the screen that asks is showing particular
/// ones: by the time a technician has typed the word, the machine may be resting on another session, and
/// a discard that took "whatever is current" would throw away something nobody was looking at.
///
/// Two round trips, as for the tray, with one more term on the token: the ids. <see cref="Confirm"/>
/// issues a token bound to exactly the sessions asked about and to the connection that asked, and says
/// what to type: DISCARD for Review's one session, the number of sessions for History's selection
/// (v0.4.1 Q6). <see cref="HandleAsync"/> spends the token against the ids the command names, whatever
/// happens next, and only then asks the machine, which discards all of them or none.
///
/// Platform-neutral, so the whole path is tested on the Mac against the real machine and store.
/// </summary>
/// <param name="time">The clock tokens expire by. The system's outside tests.</param>
public sealed class DiscardCommands(SessionMachine machine, TimeProvider? time = null)
{
    /// <summary>Review's request: one session, confirmed by typing DISCARD (Spec §5 S3).</summary>
    public const string OneSession = "discard_session";

    /// <summary>History's request: one or more sessions, confirmed by typing how many (Spec §5 S4, v0.4.1 Q6).</summary>
    public const string Sessions = "discard_sessions";

    /// <summary>
    /// The most one request may name. The History screen lists at most a thousand rows, and the token's
    /// terms are held in memory until it is spent or goes stale.
    /// </summary>
    public const int MostAtOnce = 1000;

    public const string NotConfirmed = "The confirmation was not for these sessions, or it ran out, so nothing was discarded. Try again.";

    private readonly SessionMachine _machine = machine ?? throw new ArgumentNullException(nameof(machine));

    /// <summary>
    /// Its own issuer, so a token for the tray's discard or for erase is not even looked up here, and one
    /// of these is not looked up there.
    /// </summary>
    private readonly Confirmations _confirmations = new(time);

    /// <summary>
    /// A token for exactly these sessions, and the words to type; or null when the request is not one
    /// this answers. A request with no ids is the tray's or erase's and is left to the controller. One
    /// with ids that cannot be confirmed (none, too many, a blank or a doubled id, DISCARD for more than
    /// one, or ids on an action that has none) gets nothing: a token for it would be a token for
    /// something the service cannot name.
    /// </summary>
    public ConfirmationIssued? Confirm(RequestConfirmationCommand request, Guid caller)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionIds is not { } ids || !WellFormed(ids))
        {
            return null;
        }

        var phrase = request.Action switch
        {
            OneSession when ids.Count == 1 => TypedConfirmation.DiscardWord,
            Sessions => ids.Count.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        return phrase is null
            ? null
            : new ConfirmationIssued { Token = _confirmations.Issue(DestructiveAction.DiscardSessions, caller, ids), Phrase = phrase };
    }

    /// <summary>The answer to a <see cref="DiscardSessionsCommand"/>, or null for any other command.</summary>
    public async Task<CommandResult?> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not DiscardSessionsCommand discard)
        {
            return null;
        }

        // Spent first, against the ids this command names, whether or not anything follows: a token that
        // survived a refusal, or a replay for other sessions, would be one its holder could try again.
        var ids = discard.SessionIds ?? [];
        var confirmed = _confirmations.Spend(discard.Confirmation, DestructiveAction.DiscardSessions, caller, ids) && WellFormed(ids);
        if (!confirmed)
        {
            return Refused(command, NotConfirmed);
        }

        var outcome = await _machine.DiscardSessionsAsync(ids, ct).ConfigureAwait(false);
        if (outcome.Done)
        {
            // Outside the machine's gate (review of #243): what the sessions held is already unreachable,
            // and only the deleting is left. Done before answering, so the list the screen reads next has
            // nothing of them, and so a store failure says so here rather than nowhere.
            await _machine.FinishDiscardsAsync(ct).ConfigureAwait(false);
        }

        return outcome.Done
            ? new CommandResult { RequestId = command.RequestId, Ok = true }
            : Refused(command, Explain(outcome));
    }

    /// <summary>
    /// Why nothing was discarded, in counts and plain words. No ids: they mean nothing to a technician,
    /// and the screen that asked already knows which sessions it named.
    /// </summary>
    public static string Explain(NamedDiscardOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var reasons = new List<string>();
        if (outcome.Unknown.Count > 0)
        {
            reasons.Add(outcome.Unknown.Count == 1 ? "1 session no longer exists" : $"{outcome.Unknown.Count} sessions no longer exist");
        }

        if (outcome.AlreadyDiscarded.Count > 0)
        {
            reasons.Add(outcome.AlreadyDiscarded.Count == 1 ? "1 session was already discarded" : $"{outcome.AlreadyDiscarded.Count} sessions were already discarded");
        }

        if (outcome.Published.Count > 0)
        {
            reasons.Add(outcome.Published.Count == 1
                ? "1 session has been published, and published sessions are kept"
                : $"{outcome.Published.Count} sessions have been published, and published sessions are kept");
        }

        return $"Nothing was discarded: {string.Join("; ", reasons)}. Refresh the list and try again.";
    }

    private static bool WellFormed(IReadOnlyList<string> ids) =>
        ids.Count is > 0 and <= MostAtOnce
        && ids.All(id => !string.IsNullOrWhiteSpace(id))
        && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;

    private static CommandResult Refused(IpcCommand command, string error) =>
        new() { RequestId = command.RequestId, Ok = false, Error = error };
}
