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

            default:
                return null;
        }
    }

    public Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (GetIntegrationsCommand or SearchTicketsCommand or PublishSessionCommand))
        {
            return Task.FromResult<CommandResult?>(null);
        }

        var reason = _refusals.TryRemove(command.RequestId, out var kept) ? kept : "The backend refused.";
        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = reason });
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
