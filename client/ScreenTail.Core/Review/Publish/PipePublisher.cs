using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Review.Publish;

/// <summary>
/// The publish pane's three delegates, over the pipe (ST-078 meets ST-093). The pane reasons in
/// <see cref="TicketMatch"/> and <see cref="DestinationResult"/>; this turns the service's answers into
/// those and a refusal into a failed result for every requested destination, with the reason, so the
/// pane shows it where the technician is looking rather than throwing under an open window.
/// </summary>
public sealed class PipePublisher(CaptureConnection connection)
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Provider names the tenant has connected. Empty when there are none, or no service to ask.</summary>
    public async Task<IReadOnlyList<string>> IntegrationsAsync(CancellationToken ct = default)
    {
        var listed = await _connection.RequestAsync<IntegrationsListed>(id => new GetIntegrationsCommand { RequestId = id }, ct).ConfigureAwait(false);
        return listed is null ? [] : [.. listed.Integrations.Select(i => i.Provider)];
    }

    public async Task<IReadOnlyList<TicketMatch>> SearchAsync(string query, CancellationToken ct = default)
    {
        var found = await _connection.RequestAsync<TicketsFound>(id => new SearchTicketsCommand { RequestId = id, Query = query }, ct).ConfigureAwait(false);
        return found is null ? [] : [.. found.Tickets.Select(t => new TicketMatch(t.Id, t.Summary, t.Company))];
    }

    public async Task<IReadOnlyList<DestinationResult>> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destinations = request.Destinations.ToList();
        var channel = _connection;

        // Asked as a question so the answer carries every destination; a refusal is a failed result,
        // whose words the exchange below keeps.
        string? refusal = null;
        var published = await channel.RequestAsync<SessionPublished>(
            id => new PublishSessionCommand
            {
                RequestId = id,
                SessionId = request.SessionId,
                TicketId = request.Ticket.Id,
                NoteType = request.NoteType == NoteType.Internal ? "internal" : "discussion",
                Minutes = request.Minutes,
                Destinations = [.. destinations.Select(Wire)],
                Note = request.Note,
                FrameIds = request.FrameIds,
            },
            ct).ConfigureAwait(false);

        if (published is null)
        {
            refusal ??= await ReasonAsync(request, ct).ConfigureAwait(false);
            return [.. destinations.Select(d => new DestinationResult(d, false, null, refusal))];
        }

        return [.. published.Results
            .Select(r => new DestinationResult(
                Parse(r.Destination),
                r.Ok,
                r.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri : null,
                r.Error))];
    }

    /// <summary>
    /// <see cref="CaptureConnection.RequestAsync{TReply}"/> turns a refusal into null so a screen does not
    /// crash; the reason is fetched with a plain send, which returns the failed result with its words.
    /// </summary>
    private async Task<string> ReasonAsync(PublishRequest request, CancellationToken ct)
    {
        var result = await _connection.SendAsync(
            id => new PublishSessionCommand
            {
                RequestId = id,
                SessionId = request.SessionId,
                TicketId = request.Ticket.Id,
                NoteType = request.NoteType == NoteType.Internal ? "internal" : "discussion",
                Minutes = request.Minutes,
                Destinations = [.. request.Destinations.Select(Wire)],
                Note = request.Note,
                FrameIds = request.FrameIds,
            },
            ct).ConfigureAwait(false);
        return result.Error ?? "The capture service refused to publish.";
    }

    private static string Wire(Destination destination) => destination switch
    {
        Destination.TicketNote => "ticket_note",
        Destination.TimeEntry => "time_entry",
        Destination.KbArticle => "kb_article",
        _ => throw new ArgumentOutOfRangeException(nameof(destination), destination, null),
    };

    private static Destination Parse(string wire) => wire switch
    {
        "ticket_note" => Destination.TicketNote,
        "time_entry" => Destination.TimeEntry,
        "kb_article" => Destination.KbArticle,
        _ => Destination.TicketNote,
    };
}
