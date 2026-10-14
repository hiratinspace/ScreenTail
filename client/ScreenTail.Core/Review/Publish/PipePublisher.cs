using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Review.Publish;

/// <summary>
/// The publish pane's three delegates, over the pipe (ST-078 meets ST-093). The pane reasons in
/// <see cref="TicketMatch"/> and <see cref="DestinationResult"/>; this turns the service's answers into
/// those and a refusal into a failed result for every requested destination, with the reason, so the
/// pane shows it where the technician is looking rather than throwing under an open window. A link is
/// kept only when it is one the pane may hand to the shell (<see cref="ExternalLinks.IsWebLink"/>).
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

    /// <summary>The documentation platform's companies, for the mapping prompt (ST-097). Empty when the service would not say.</summary>
    public async Task<IReadOnlyList<CompanyChoice>> CompaniesAsync(CancellationToken ct = default)
    {
        var listed = await _connection.RequestAsync<CompanyMappingsListed>(id => new GetCompanyMappingsCommand { RequestId = id }, ct).ConfigureAwait(false);
        return listed is null ? [] : [.. listed.Companies.Select(c => new CompanyChoice(c.Id, c.Name))];
    }

    /// <summary>The prompt's answer, remembered by the backend. False when it was refused.</summary>
    public async Task<bool> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default)
    {
        var result = await _connection.SendAsync(id => new MapCompanyCommand { RequestId = id, PsaCompany = psaCompany, DocCompanyId = docCompanyId }, ct).ConfigureAwait(false);
        return result.Ok;
    }

    public async Task<IReadOnlyList<DestinationResult>> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destinations = request.Destinations.ToList();

        // Asked once, whatever comes back (S-36). A refusal's words arrive on this same exchange; asking
        // again to learn them would publish again, and a first ask that timed out after landing would
        // then put a second note on the customer's ticket that nobody clicked for (INV-3).
        var answer = await _connection.AskAsync<SessionPublished>(
            id => new PublishSessionCommand
            {
                RequestId = id,
                SessionId = request.SessionId,
                TicketId = request.Ticket.Id,
                TicketCompany = request.Ticket.Company,
                NoteType = request.NoteType == NoteType.Internal ? "internal" : "discussion",
                Minutes = request.Minutes,
                Destinations = [.. destinations.Select(Wire)],
                Note = request.Note,
                FrameIds = request.FrameIds,
            },
            ct).ConfigureAwait(false);

        if (answer.Reply is not { } published)
        {
            return [.. destinations.Select(d => new DestinationResult(d, false, null, answer.Refusal))];
        }

        return [.. published.Results
            .Select(r => new DestinationResult(
                Parse(r.Destination),
                r.Ok,
                r.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && ExternalLinks.IsWebLink(uri) ? uri : null,
                r.Error,
                r.Kind))];
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
