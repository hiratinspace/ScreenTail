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
                PublishAgain = request.PublishAgain,
            },
            ct).ConfigureAwait(false);

        // Sent, and the pipe went before the service answered: the backend may have posted it all. Said as
        // that, per destination, so the pane holds every way of sending it again until the technician has
        // looked, rather than offering a plain failure to publish over (compare BlurWithReplyAsync).
        if (answer.WasLost)
        {
            return [.. destinations.Select(d => new DestinationResult(d, false, null, Lost(d), DestinationResult.Unknown))];
        }

        // The service's own two refusals that mean "it may be there already": a session it has marked
        // published, and one whose earlier publish is still on its way. The same outcome as a lost answer.
        if (answer.Reply is not { } published)
        {
            var kind = answer.Refusal switch
            {
                PublishCommands.AlreadyPublished => DestinationResult.AlreadyPublished,
                PublishCommands.Unconfirmed => DestinationResult.SessionUnconfirmed,
                PublishCommands.StillPublishing => DestinationResult.Unknown,
                _ => DestinationResult.Refused,
            };
            return [.. destinations.Select(d => new DestinationResult(d, false, null, answer.Refusal, kind))];
        }

        return [.. published.Results
            .Select(r => new DestinationResult(
                Parse(r.Destination),
                r.Ok,
                r.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && ExternalLinks.IsWebLink(uri) ? uri : null,
                r.Error,
                r.Kind))];
    }

    /// <summary>The backend's own wording for a destination that may have landed, with the cause said first.</summary>
    private static string Lost(Destination destination) => destination switch
    {
        Destination.TicketNote => "The connection to the capture service dropped before it answered, so the note may already be on the ticket. Check the ticket before you publish the note again.",
        Destination.TimeEntry => "The connection to the capture service dropped before it answered, so the time entry may already be on the ticket. Check the ticket before you publish the time entry again.",
        _ => "The connection to the capture service dropped before it answered, so the article may already be written. Check the knowledge base before you publish the article again.",
    };

    /// <summary>
    /// The session reply's <c>publish_outcomes</c> as the pane's (O3). A destination or an outcome this
    /// version does not know is left out rather than guessed at.
    /// </summary>
    public static IReadOnlyDictionary<Destination, EarlierOutcome> Earlier(IReadOnlyDictionary<string, string>? outcomes)
    {
        var earlier = new Dictionary<Destination, EarlierOutcome>();
        foreach (var (wire, outcome) in outcomes ?? new Dictionary<string, string>())
        {
            Destination? destination = wire switch
            {
                "ticket_note" => Destination.TicketNote,
                "time_entry" => Destination.TimeEntry,
                "kb_article" => Destination.KbArticle,
                _ => null,
            };
            EarlierOutcome? known = outcome switch
            {
                "landed" => EarlierOutcome.Landed,
                "unknown" => EarlierOutcome.Unknown,
                _ => null,
            };
            if (destination is { } d && known is { } o)
            {
                earlier[d] = o;
            }
        }

        return earlier;
    }

    /// <summary>What the service remembers the session's earlier publishes left, asked again after a refusal (O3). Empty when it will not say.</summary>
    public async Task<IReadOnlyDictionary<Destination, EarlierOutcome>> EarlierAsync(string sessionId, CancellationToken ct = default)
    {
        var loaded = await _connection.RequestAsync<SessionLoaded>(id => new GetSessionCommand { RequestId = id, SessionId = sessionId }, ct).ConfigureAwait(false);
        return Earlier(loaded?.PublishOutcomes);
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
