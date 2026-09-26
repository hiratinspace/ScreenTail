using System.Collections.Concurrent;
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
public sealed class PublishCommands(ISessionStore store, IPsaGateway gateway)
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IPsaGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private readonly ConcurrentDictionary<int, string> _refusals = new();

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

        var frames = new List<PublishWireFrame>(publish.FrameIds.Count);
        foreach (var id in publish.FrameIds)
        {
            // Redacted frames only: the store will not hand back a pending one, and one deleted between
            // the technician's choice and now is simply not sent (INV-1).
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

        return answer.Ok
            ? new SessionPublished { RequestId = publish.RequestId, Results = answer.Value! }
            : Refuse(publish, answer.Refusal!);
    }

    private IpcEvent? Refuse(IpcCommand command, string reason)
    {
        _refusals[command.RequestId] = reason;
        return null;
    }
}
