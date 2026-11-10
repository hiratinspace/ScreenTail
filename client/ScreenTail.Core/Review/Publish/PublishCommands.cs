using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ScreenTail.Core.Net;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Review.Publish;

/// <summary>
/// The service's side of publishing over the pipe (ST-093, ST-094). The UI names the session, the
/// ticket, the destinations and the frames it kept; this reads the frames' bytes from the store — only
/// redacted frames have any (INV-1) — and asks the backend, which holds the tenant's PSA. The backend's
/// per-destination answer is passed back as it is.
///
/// Same two entry points as <see cref="ReviewCommands"/>: a question answered with its event, and the
/// reason when there was no answer. The refusal is kept by connection and request id between the two
/// calls, because the server asks them in that order on the same connection, and every client numbers
/// its requests from 1 (review of #265).
/// </summary>
/// <param name="published">
/// Told once a publish has marked its session published, so the machine stops resting on it and the
/// tray's count moves (second review of #243). The machine's <c>PublishedAsync</c> in the service.
/// </param>
/// <param name="edits">
/// The turn <see cref="ReviewCommands"/> takes to blur a frame. The frames' bytes are read inside it, so a
/// publish that arrives while a blur is being painted waits and sends the blurred picture, never the one
/// from before (ST-084 follow-up review).
/// </param>
/// <param name="outcomesChanged">
/// Told after a session's publish record is written, so the tray's count of publishes not confirmed
/// follows it (contract v10). The machine's <c>PublishOutcomesChangedAsync</c> in the service.
/// </param>
public sealed class PublishCommands(
    ISessionStore store,
    IPsaGateway gateway,
    IMetricsReporter? metrics = null,
    Intel.IStyleLearner? style = null,
    ILogger? log = null,
    Func<string, CancellationToken, Task<bool>>? published = null,
    FrameEditGate? edits = null,
    Func<CancellationToken, Task>? outcomesChanged = null)
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IPsaGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private readonly ConcurrentDictionary<(Guid Caller, int RequestId), string> _refusals = new();

    /// <summary>Sessions with a publish on its way to the backend. A second one waits for none: it is refused.</summary>
    private readonly ConcurrentDictionary<string, byte> _publishing = new(StringComparer.Ordinal);

    /// <summary>The backend's ceiling on frames per publish (PublishLimits.MaxFrames, which is BundleOptions.MaxFrames).</summary>
    public const int MaxFrames = 25;

    /// <summary>
    /// The refusal for a destination the store holds as landed, sent without <c>publish_again</c>: a plain
    /// resend (a dropped pipe, a restarted UI, a second window that never saw the landing) would add it
    /// twice. Also every destination of a session published before the store kept outcomes.
    /// </summary>
    public const string AlreadyPublished = "This session is already published, so it may already be on the ticket. Check the ticket, then confirm before publishing again, so nothing is added twice.";

    /// <summary>
    /// The refusal for a destination the store holds as unknown (O2), one whose last publish may have
    /// landed, sent without <c>publish_again</c>. Kept in the store and checked for each destination sent,
    /// so it holds across a reopened Review and a service restart, and against a second window that knows
    /// of a different destination's outcome: that window's flag is only sent once its technician has
    /// ticked the check.
    /// </summary>
    public const string Unconfirmed = "An earlier publish of this session was not confirmed, so it may already be on the ticket. Check the ticket, then confirm before publishing again, so nothing is added twice.";

    /// <summary>The refusal while another publish of the same session has not answered yet, whatever the flag says.</summary>
    public const string StillPublishing = "This session is still being published, so it may already be on the ticket. Wait a moment, check the ticket, then confirm before publishing again.";

    /// <summary>The refusal for a destination name this version does not know, said before anything is written ahead (m-b).</summary>
    public const string UnknownDestination = "The publish named a destination this version of ScreenTail does not know, so nothing was sent.";

    /// <summary>
    /// The refusal of <c>mark_published</c> for a session with no destination held as unknown: a draft
    /// never published, a session published cleanly, one already settled, or one published before the
    /// store kept outcomes (nothing says which destinations it would be claiming).
    /// </summary>
    public const string NothingToConfirm = "No publish of this session is waiting to be confirmed, so nothing was changed.";

    /// <summary>The refusal of <c>mark_published</c> for a discarded session: discard is the last word (INV-12).</summary>
    public const string DiscardedCannotBeMarked = "This session was discarded, so it cannot be marked as published.";

    /// <summary>The refusal of <c>mark_published</c> while a publish of the session has not answered: its answer will say what happened.</summary>
    public const string StillPublishingCannotBeMarked = "This session is still being published. Wait for it to finish, then check the ticket.";

    /// <summary>The refusal of <c>mark_published</c> for a session the store does not hold.</summary>
    public const string GoneCannotBeMarked = "The session is gone, so there is nothing to mark as published.";

    /// <summary>The kind of fault the last post-publish bookkeeping hit, or null. Never its message: a store's words may quote what it held.</summary>
    public string? LastBookkeepingError { get; private set; }

    /// <summary>For one caller only, as the tests that ask on no connection do.</summary>
    public Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default) => ReplyToAsync(command, Guid.Empty, ct);

    /// <param name="caller">The connection asking, which keys the refusal its <see cref="HandleAsync(IpcCommand, Guid, CancellationToken)"/> reads.</param>
    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case GetIntegrationsCommand:
                {
                    var answer = await _gateway.IntegrationsAsync(ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new IntegrationsListed { RequestId = command.RequestId, Integrations = answer.Value! }
                        : Refuse(caller, command, answer.Refusal!);
                }

            case SearchTicketsCommand search:
                {
                    var answer = await _gateway.SearchTicketsAsync(search.Query, ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new TicketsFound { RequestId = command.RequestId, Tickets = answer.Value! }
                        : Refuse(caller, command, answer.Refusal!);
                }

            case PublishSessionCommand publish:
                return await PublishAsync(publish, caller, ct).ConfigureAwait(false);

            case GetCompanyMappingsCommand:
                {
                    var answer = await _gateway.CompanyMappingsAsync(ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new CompanyMappingsListed { RequestId = command.RequestId, Companies = answer.Value!.Companies, Mappings = answer.Value.Mappings }
                        : Refuse(caller, command, answer.Refusal!);
                }

            case ListIntegrationsCommand:
                {
                    var answer = await _gateway.IntegrationDetailsAsync(ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new IntegrationDetailsListed { RequestId = command.RequestId, Integrations = answer.Value! }
                        : Refuse(caller, command, answer.Refusal!);
                }

            case CheckIntegrationCommand check:
                {
                    var answer = await _gateway.CheckIntegrationAsync(check.Provider, ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new IntegrationChecked { RequestId = command.RequestId, Ok = answer.Value!.Ok, Message = answer.Value.Message }
                        : Refuse(caller, command, answer.Refusal!);
                }

            default:
                return null;
        }
    }

    /// <summary>For one caller only, as the tests that ask on no connection do.</summary>
    public Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default) => HandleAsync(command, Guid.Empty, ct);

    /// <param name="caller">The connection asking: the same one whose question was refused.</param>
    public async Task<CommandResult?> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is MarkPublishedCommand mark)
        {
            var refusal = await MarkAsPublishedAsync(mark.SessionId, ct).ConfigureAwait(false);
            return new CommandResult { RequestId = command.RequestId, Ok = refusal is null, Error = refusal };
        }

        // The actions, whose result is the answer (the questions above are answered with their event).
        GatewayAnswer<bool>? acted = command switch
        {
            MapCompanyCommand map => await _gateway.MapCompanyAsync(map.PsaCompany, map.DocCompanyId, ct).ConfigureAwait(false),
            UnmapCompanyCommand unmap => await _gateway.UnmapCompanyAsync(unmap.PsaCompany, ct).ConfigureAwait(false),
            StoreIntegrationCommand store => await _gateway.StoreIntegrationAsync(store.Provider, store.SiteUrl, store.Secret, ct).ConfigureAwait(false),
            RemoveIntegrationCommand remove => await _gateway.RemoveIntegrationAsync(remove.Provider, ct).ConfigureAwait(false),
            _ => null,
        };
        if (acted is { } answer)
        {
            return new CommandResult { RequestId = command.RequestId, Ok = answer.Ok, Error = answer.Refusal };
        }

        if (command is not (GetIntegrationsCommand or SearchTicketsCommand or PublishSessionCommand or GetCompanyMappingsCommand or ListIntegrationsCommand or CheckIntegrationCommand))
        {
            return null;
        }

        var reason = _refusals.TryRemove((caller, command.RequestId), out var kept) ? kept : "The backend refused.";
        return new CommandResult { RequestId = command.RequestId, Ok = false, Error = reason };
    }

    private async Task<IpcEvent?> PublishAsync(PublishSessionCommand publish, Guid caller, CancellationToken ct)
    {
        // A pipe that dropped mid-publish leaves the first one running here. A technician who looks at
        // the ticket before it lands sees nothing and confirms; refused even then, until it has answered.
        if (!_publishing.TryAdd(publish.SessionId, 0))
        {
            return Refuse(caller, publish, StillPublishing);
        }

        try
        {
            return await PublishOnceAsync(publish, caller, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _publishing.TryRemove(publish.SessionId, out _);
        }
    }

    private async Task<IpcEvent?> PublishOnceAsync(PublishSessionCommand publish, Guid caller, CancellationToken ct)
    {
        var session = await _store.LoadSessionAsync(publish.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return Refuse(caller, publish, "The session is gone, so there is nothing to publish.");
        }

        // The note comes from the pane, not the store, so a session the technician threw away could still
        // go out with whatever the window held. A discard is the last word on it (INV-12).
        var state = (await _store.GetSessionSummaryAsync(publish.SessionId, ct).ConfigureAwait(false))?.State;
        if (string.Equals(state, Sessions.SessionStateNames.Discarded, StringComparison.Ordinal))
        {
            return Refuse(caller, publish, "The session was discarded, so there is nothing to publish.");
        }

        // m-b: the record is keyed by destination, and a name nothing answers for would be written ahead
        // as unknown and stay so for good.
        if (publish.Destinations.Any(d => !KnownDestinations.Contains(d)))
        {
            return Refuse(caller, publish, UnknownDestination);
        }

        // Something of it may be on a ticket already. The backend keeps nothing to catch a duplicate with
        // (INV-7), so this is where it is caught, for each destination sent, against what the store holds
        // for it: landed, or unknown (O2), whichever window, pane or service run left it (MJ1). Only the
        // technician's "I checked the ticket" says a resend is meant, and the pane says so with
        // publish_again (a Retry of what plainly failed needs no flag: nothing of it is held).
        var stored = (await _store.GetPublishRecordAsync(publish.SessionId, ct).ConfigureAwait(false))?.Outcomes ?? new Dictionary<string, string>();
        var isPublished = string.Equals(state, Sessions.SessionStateNames.Published, StringComparison.Ordinal);

        // minor-2: a service that died between saving the outcomes and marking the session published left
        // a landed session as a draft, which the tray and History keep offering. Mended here, whatever this
        // publish then does. Not swept at service start: this is the first moment anything acts on it.
        if (!isPublished && stored.Values.Contains(PublishRecord.Landed, StringComparer.Ordinal))
        {
            await KeepingTheResultAsync("state", () => MarkPublishedAsync(publish.SessionId, ct)).ConfigureAwait(false);
            isPublished = true;
        }

        // minor-1: a session published before the record existed (0013) says nothing about which
        // destination landed, so every one starts as unknown. Written ahead with the rest below, so one new
        // publish of the article does not leave the note and the time entry looking never sent.
        var before = stored.Count == 0 && isPublished ? AllUnknown() : stored;
        if (!publish.PublishAgain && Refusal(publish.Destinations, stored, state) is { } refusal)
        {
            return Refuse(caller, publish, refusal);
        }

        // The UI's list is a choice among frames, not a licence: only this session's frames that pass the
        // drafting bundle's own rule may go (redacted, not sensitive, not excluded, not taken while capture
        // was off). Anything else named is dropped, and so is a second mention of the same frame.
        var eligible = Intel.BundleBuilder.Eligible(session).Select(frame => frame.Id).ToHashSet(StringComparer.Ordinal);
        var chosen = new List<string>(Math.Min(publish.FrameIds.Count, eligible.Count));
        foreach (var id in publish.FrameIds)
        {
            if (eligible.Remove(id))
            {
                chosen.Add(id);
            }
        }

        // E-18: the backend takes at most this many and refuses the whole publish above it. Counted on what
        // would be attached, and before a single image is read, so a long session is told what to do rather
        // than encoding and uploading megabytes for a certain refusal.
        if (chosen.Count > MaxFrames)
        {
            return Refuse(caller, publish, $"A publish can carry at most {MaxFrames} frames, and {chosen.Count} are included. Exclude {chosen.Count - MaxFrames} in the filmstrip and publish again.");
        }

        var frames = new List<PublishWireFrame>(chosen.Count);
        using (edits is null ? null : await edits.EnterAsync(ct).ConfigureAwait(false))
        {
            foreach (var id in chosen)
            {
                // The store will still not hand back a pending one, and one deleted between the technician's
                // choice and now is simply not sent (INV-1).
                var image = await _store.GetRedactedFrameImageAsync(id, ct).ConfigureAwait(false);
                if (image is { Length: > 0 })
                {
                    frames.Add(new PublishWireFrame(id, Convert.ToBase64String(image)));
                }
            }
        }

        // Written ahead: from here the request may leave, so until the backend's answer says otherwise every
        // destination sent is unknown. A fault, a cancelled connection or a service that dies mid-publish
        // leaves it so, and the next publish is refused until the technician has looked (O2).
        // One window stays, accepted by the owner (decision 4 on #265): the store runs WAL with
        // synchronous = NORMAL (SqliteSessionStore.OpenAsync), so a commit is durable against a crashed
        // process but not against power loss before the next checkpoint. A machine that loses power
        // mid-publish can come back without this row, and the next plain publish is then not refused.
        // synchronous stays NORMAL: FULL would cost every capture write a sync for this one case.
        await SaveRecordAsync(publish.SessionId, Merge(before, publish.Destinations.Select(d => (d, PublishRecord.Unknown))), ct).ConfigureAwait(false);

        var answer = await _gateway.PublishAsync(
            new PublishWire
            {
                SessionId = publish.SessionId,
                TicketId = publish.TicketId,
                Company = publish.TicketCompany,
                NoteType = publish.NoteType,
                Minutes = publish.Minutes,
                StartedAt = session.StartedAt,
                Billable = publish.Billable,
                Reviewer = null,
                Footer = true,
                Destinations = publish.Destinations,
                Note = publish.Note,
                Frames = frames,
            },
            ct).ConfigureAwait(false);

        if (!answer.Ok)
        {
            // A plain refusal: nothing was sent, so the record goes back to how it was.
            await KeepingTheResultAsync("outcomes", () => SaveRecordAsync(publish.SessionId, before, ct)).ConfigureAwait(false);
            return Refuse(caller, publish, answer.Refusal!);
        }

        // What the backend says about each destination replaces the write-ahead. One it did not mention
        // stays unknown.
        var written = Merge(before, publish.Destinations.Select(d => (d, PublishRecord.Unknown)));
        foreach (var row in answer.Value!)
        {
            if (row.Ok)
            {
                written[row.Destination] = PublishRecord.Landed;
            }
            else if (row.Kind is DestinationResult.Unknown or DestinationResult.TimedOut)
            {
                written[row.Destination] = Settled(before, row.Destination) ?? PublishRecord.Unknown;
            }
            else if (Settled(before, row.Destination) is null)
            {
                // A plain failure, sent after the technician checked: nothing of it is on the ticket.
                _ = written.Remove(row.Destination);
            }
            else
            {
                written[row.Destination] = PublishRecord.Landed;
            }
        }

        await KeepingTheResultAsync("outcomes", () => SaveRecordAsync(publish.SessionId, written, ct)).ConfigureAwait(false);

        // ST-098: the session counts as published once any destination landed. Counts and times only.
        // ST-067: and what the technician changed about the draft is learned now, as signals, never words.
        // S-37: by now the note is on the ticket, so neither may stand between the technician and the
        // backend's results. A fault in either is kept and the results go back as they came.
        if (answer.Value!.Any(r => r.Ok))
        {
            // Spec §5 S4's Published, and what keeps the session from a discard: its note is in the PSA
            // now (owner decision, fail closed). The write refuses a row discarded while the backend was
            // answering, so a late landing does not bring a discarded session back. Bookkeeping as well, so
            // a store fault here cannot hide the results either.
            await KeepingTheResultAsync("state", () => MarkPublishedAsync(publish.SessionId, ct)).ConfigureAwait(false);

            if (metrics is not null)
            {
                await KeepingTheResultAsync("metrics", () => metrics.ReportAsync(publish.SessionId, published: true, ct)).ConfigureAwait(false);
            }

            if (style is not null)
            {
                await KeepingTheResultAsync("style", () => style.LearnAsync(publish.SessionId, ct)).ConfigureAwait(false);
            }
        }

        return new SessionPublished { RequestId = publish.RequestId, Results = answer.Value! };
    }

    /// <summary>Marks the session published and, when the write took, tells the machine so the tray's count moves.</summary>
    /// <returns>Whether the write took. False for a discarded row, which stays discarded (INV-12).</returns>
    private async Task<bool> MarkPublishedAsync(string sessionId, CancellationToken ct)
    {
        if (!await _store.MarkPublishedAsync(sessionId, ct).ConfigureAwait(false))
        {
            return false;
        }

        if (published is not null)
        {
            // The row is written. A machine that cannot be told is bookkeeping, and is kept as such.
            await KeepingTheResultAsync("state", () => published(sessionId, ct)).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// <c>mark_published</c> (contract v10): the technician checked the ticket and what an earlier publish
    /// may have left is there. Every destination held as unknown becomes landed, the unconfirmed mark goes
    /// with them, and the session is marked published through the same write a landing makes. Nothing is
    /// sent. A statement about the ticket, taken on the technician's word as the "I checked" tick is.
    /// </summary>
    /// <returns>Why it was refused, or null when it was done.</returns>
    private async Task<string?> MarkAsPublishedAsync(string sessionId, CancellationToken ct)
    {
        // The slot a publish takes, so neither runs under the other: a publish on its way will say what
        // happened by itself, and a publish that starts now waits to be refused by what this writes.
        if (!_publishing.TryAdd(sessionId, 0))
        {
            return StillPublishingCannotBeMarked;
        }

        try
        {
            var state = (await _store.GetSessionSummaryAsync(sessionId, ct).ConfigureAwait(false))?.State;
            if (state is null)
            {
                return GoneCannotBeMarked;
            }

            if (string.Equals(state, Sessions.SessionStateNames.Discarded, StringComparison.Ordinal))
            {
                return DiscardedCannotBeMarked;
            }

            // Only a doubt the store holds. A session published before the record existed has none
            // written: nothing says which destinations were ever sent, and settling it would claim all
            // three. One new publish of it writes them down, and then it is a doubt like any other.
            var stored = (await _store.GetPublishRecordAsync(sessionId, ct).ConfigureAwait(false))?.Outcomes ?? new Dictionary<string, string>();
            if (!stored.Values.Contains(PublishRecord.Unknown, StringComparer.Ordinal))
            {
                return NothingToConfirm;
            }

            // The state first. The write refuses a row discarded since it was read above (INV-12), and a
            // service that dies between the two writes leaves the session published and still in doubt,
            // which History goes on showing and a second mark finishes. The other order would leave a
            // settled record on a draft, with nothing saying anything was left to do.
            //
            // Whether it was counted already is read from the record, not the state (review of #274): a
            // mark cut off between its two writes left the state published with nothing reported, and
            // its retry, seeing "published", never reported it. A landing is what reports a session, and
            // a landing is written down, so a record with nothing landed means nothing was reported. The
            // metric is keyed by session in the outbox, so a retry after a report cannot count twice.
            var wasCounted = stored.Values.Contains(PublishRecord.Landed, StringComparer.Ordinal);
            if (!await MarkPublishedAsync(sessionId, ct).ConfigureAwait(false))
            {
                return DiscardedCannotBeMarked;
            }

            var settled = stored.ToDictionary(
                outcome => outcome.Key,
                outcome => string.Equals(outcome.Value, PublishRecord.Unknown, StringComparison.Ordinal) ? PublishRecord.Landed : outcome.Value,
                StringComparer.Ordinal);
            await SaveRecordAsync(sessionId, settled, ct).ConfigureAwait(false);

            // ST-098, ST-067: a session nothing had landed for counts as published from now. One with a
            // landing on record was counted when it landed.
            if (!wasCounted)
            {
                if (metrics is not null)
                {
                    await KeepingTheResultAsync("metrics", () => metrics.ReportAsync(sessionId, published: true, ct)).ConfigureAwait(false);
                }

                if (style is not null)
                {
                    await KeepingTheResultAsync("style", () => style.LearnAsync(sessionId, ct)).ConfigureAwait(false);
                }
            }

            return null;
        }
        finally
        {
            _ = _publishing.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// Writes the session's publish record, then has the tray's count of publishes not confirmed read
    /// again. The telling never stands in a publish's way: a fault in it is kept as its kind alone.
    /// </summary>
    private async Task SaveRecordAsync(string sessionId, IReadOnlyDictionary<string, string> outcomes, CancellationToken ct)
    {
        _ = await _store.SavePublishRecordAsync(sessionId, outcomes, ct).ConfigureAwait(false);
        if (outcomesChanged is null)
        {
            return;
        }

        try
        {
            await outcomesChanged(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LastBookkeepingError = error.GetType().Name;
            if (log is not null)
            {
                CountNotTold(log, LastBookkeepingError, null);
            }
        }
    }

    /// <summary>Every destination as unknown, for a session published before the record existed.</summary>
    private static Dictionary<string, string> AllUnknown() =>
        KnownDestinations.ToDictionary(d => d, _ => PublishRecord.Unknown, StringComparer.Ordinal);

    private static readonly HashSet<string> KnownDestinations = new(["ticket_note", "time_entry", "kb_article"], StringComparer.Ordinal);

    /// <summary>
    /// Why a plain send of these destinations is refused, or null. <see cref="AlreadyPublished"/> when any of
    /// them landed, <see cref="Unconfirmed"/> when any may have. A session published before the record
    /// existed (migration 0013) has the state and no outcomes, so every destination may have landed.
    /// </summary>
    private static string? Refusal(IReadOnlyList<string> destinations, IReadOnlyDictionary<string, string> before, string? state)
    {
        if (before.Count == 0)
        {
            return string.Equals(state, Sessions.SessionStateNames.Published, StringComparison.Ordinal) ? AlreadyPublished : null;
        }

        var held = destinations.Select(d => before.TryGetValue(d, out var outcome) ? outcome : null).ToList();
        if (held.Contains(PublishRecord.Landed, StringComparer.Ordinal))
        {
            return AlreadyPublished;
        }

        return held.Contains(PublishRecord.Unknown, StringComparer.Ordinal) ? Unconfirmed : null;
    }

    /// <summary>The earlier outcomes with these laid over them, except that a destination that landed stays landed: that is a fact.</summary>
    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> before, IEnumerable<(string Destination, string Outcome)> now)
    {
        var merged = new Dictionary<string, string>(before, StringComparer.Ordinal);
        foreach (var (destination, outcome) in now)
        {
            merged[destination] = Settled(before, destination) ?? outcome;
        }

        return merged;
    }

    /// <summary><see cref="PublishRecord.Landed"/> when an earlier publish put this destination on the ticket, otherwise null.</summary>
    private static string? Settled(IReadOnlyDictionary<string, string> before, string destination) =>
        before.TryGetValue(destination, out var was) && string.Equals(was, PublishRecord.Landed, StringComparison.Ordinal) ? was : null;

    /// <summary>Runs one piece of post-publish bookkeeping; a fault is kept and logged as its kind alone (INV-10).</summary>
    private async Task KeepingTheResultAsync(string what, Func<Task> bookkeeping)
    {
        try
        {
            await bookkeeping().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The type, never the message or the exception itself: a store's words may quote what it held,
            // and the session id is not the log's business either.
            LastBookkeepingError = error.GetType().Name;
            if (log is not null)
            {
                BookkeepingFailed(log, what, LastBookkeepingError, null);
            }
        }
    }

    private static readonly Action<ILogger, string, string, Exception?> BookkeepingFailed = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(1, nameof(BookkeepingFailed)),
        "A publish landed, but its {Bookkeeping} bookkeeping failed with {Fault}; the results went back as they came");

    private static readonly Action<ILogger, string, Exception?> CountNotTold = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2, nameof(CountNotTold)),
        "A publish record was written, but the count of publishes not confirmed could not be read again ({Fault}); the tray may be behind until the next change");

    private IpcEvent? Refuse(Guid caller, IpcCommand command, string reason)
    {
        _refusals[(caller, command.RequestId)] = reason;
        return null;
    }
}
