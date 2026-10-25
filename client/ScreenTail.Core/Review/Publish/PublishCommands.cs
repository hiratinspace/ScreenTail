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
/// reason when there was no answer. The refusal is kept by request id between the two calls, because
/// the server asks them in that order on the same connection.
/// </summary>
public sealed class PublishCommands(ISessionStore store, IPsaGateway gateway, IMetricsReporter? metrics = null, Intel.IStyleLearner? style = null, ILogger? log = null)
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IPsaGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private readonly ConcurrentDictionary<int, string> _refusals = new();

    /// <summary>The backend's ceiling on frames per publish (PublishLimits.MaxFrames, which is BundleOptions.MaxFrames).</summary>
    public const int MaxFrames = 25;

    /// <summary>The kind of fault the last post-publish bookkeeping hit, or null. Never its message: a store's words may quote what it held.</summary>
    public string? LastBookkeepingError { get; private set; }

    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case GetIntegrationsCommand:
                {
                    var answer = await _gateway.IntegrationsAsync(ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new IntegrationsListed { RequestId = command.RequestId, Integrations = answer.Value! }
                        : Refuse(command, answer.Refusal!);
                }

            case SearchTicketsCommand search:
                {
                    var answer = await _gateway.SearchTicketsAsync(search.Query, ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new TicketsFound { RequestId = command.RequestId, Tickets = answer.Value! }
                        : Refuse(command, answer.Refusal!);
                }

            case PublishSessionCommand publish:
                return await PublishAsync(publish, ct).ConfigureAwait(false);

            case GetCompanyMappingsCommand:
                {
                    var answer = await _gateway.CompanyMappingsAsync(ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new CompanyMappingsListed { RequestId = command.RequestId, Companies = answer.Value!.Companies, Mappings = answer.Value.Mappings }
                        : Refuse(command, answer.Refusal!);
                }

            case ListIntegrationsCommand:
                {
                    var answer = await _gateway.IntegrationDetailsAsync(ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new IntegrationDetailsListed { RequestId = command.RequestId, Integrations = answer.Value! }
                        : Refuse(command, answer.Refusal!);
                }

            case CheckIntegrationCommand check:
                {
                    var answer = await _gateway.CheckIntegrationAsync(check.Provider, ct).ConfigureAwait(false);
                    return answer.Ok
                        ? new IntegrationChecked { RequestId = command.RequestId, Ok = answer.Value!.Ok, Message = answer.Value.Message }
                        : Refuse(command, answer.Refusal!);
                }

            default:
                return null;
        }
    }

    public async Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
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

        var reason = _refusals.TryRemove(command.RequestId, out var kept) ? kept : "The backend refused.";
        return new CommandResult { RequestId = command.RequestId, Ok = false, Error = reason };
    }

    private async Task<IpcEvent?> PublishAsync(PublishSessionCommand publish, CancellationToken ct)
    {
        var session = await _store.LoadSessionAsync(publish.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return Refuse(publish, "The session is gone, so there is nothing to publish.");
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
            return Refuse(publish, $"A publish can carry at most {MaxFrames} frames, and {chosen.Count} are included. Exclude {chosen.Count - MaxFrames} in the filmstrip and publish again.");
        }

        var frames = new List<PublishWireFrame>(chosen.Count);
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
            return Refuse(publish, answer.Refusal!);
        }

        // ST-098: the session counts as published once any destination landed. Counts and times only.
        // ST-067: and what the technician changed about the draft is learned now, as signals, never words.
        // S-37: by now the note is on the ticket, so neither may stand between the technician and the
        // backend's results. A fault in either is kept and the results go back as they came.
        if (answer.Value!.Any(r => r.Ok))
        {
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

    private IpcEvent? Refuse(IpcCommand command, string reason)
    {
        _refusals[command.RequestId] = reason;
        return null;
    }
}
