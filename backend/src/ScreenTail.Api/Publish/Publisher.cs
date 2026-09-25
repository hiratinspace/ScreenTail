using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Publish;

/// <summary>
/// Sends each destination to the tenant's PSA and answers each on its own (ST-093, ST-094). A failure in
/// one is not a failure of the others: the note that landed stays landed, and the response says exactly
/// which part did not, so the client's Retry sends only that. INV-3: this runs because a technician
/// pressed Publish, and for no other reason.
/// </summary>
public static class Publisher
{
    /// <param name="docs">The tenant's documentation platform, or null when none is connected: the KB destination then says so and the rest still goes.</param>
    /// <param name="mappings">Where a PSA company's Hudu company is remembered (ST-097). Null in tests that have no KB destination.</param>
    public static async Task<PublishResponse> PublishAsync(IPsaProvider psa, PublishBundle bundle, IDocProvider? docs = null, CompanyMappings? mappings = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(psa);
        ArgumentNullException.ThrowIfNull(bundle);
        var text = NoteFormatter.Render(bundle.Note, [.. bundle.Frames.Select(f => f.Id)], bundle.Reviewer, bundle.Footer);
        var results = new List<PublishResultJson>();

        foreach (var destination in bundle.Destinations.Distinct(StringComparer.Ordinal))
        {
            results.Add(destination switch
            {
                Destinations.TicketNote => Answer(destination, await psa.AddNoteAsync(
                    new TicketNote(
                        bundle.TicketId,
                        text,
                        Internal: bundle.NoteType == "internal",
                        [.. bundle.Frames.Select((frame, i) => new NoteAttachment(
                            $"screenshot-{i + 1}{Extension(frame.MediaType)}",
                            frame.MediaType,
                            Convert.FromBase64String(frame.Image)))]),
                    ct).ConfigureAwait(false)),

                Destinations.TimeEntry => Answer(destination, await psa.AddTimeEntryAsync(
                    new TimeEntry(bundle.TicketId, bundle.StartedAt ?? DateTimeOffset.UtcNow, bundle.Minutes, text, bundle.Billable),
                    ct).ConfigureAwait(false)),

                // Said per destination rather than refusing the whole publish, so the note and the time
                // still go whatever the knowledge base thinks.
                Destinations.KbArticle => docs is null || mappings is null
                    ? new PublishResultJson(destination, false, Error: "No documentation platform is connected, so no article was written. The note and the time entry are unaffected.", Kind: "not_configured")
                    : await ArticleAsync(destination, docs, mappings, bundle, ct).ConfigureAwait(false),

                _ => new PublishResultJson(destination, false, Error: "That is not a destination.", Kind: "invalid"),
            });
        }

        return new PublishResponse(results);
    }

    /// <summary>
    /// The article: the ticket's company mapped to the platform's — remembered, or exact and then
    /// remembered — or a mapping asked for. A likely match is offered in the message and never assumed:
    /// the wrong company is a customer's runbook in another customer's knowledge base.
    /// </summary>
    private static async Task<PublishResultJson> ArticleAsync(string destination, IDocProvider docs, CompanyMappings mappings, PublishBundle bundle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bundle.Company))
        {
            return new PublishResultJson(destination, false, Error: "The ticket has no company, so there is nowhere to file the article.", Kind: "needs_mapping");
        }

        var mapped = await mappings.FindAsync(bundle.Company, ct).ConfigureAwait(false);
        if (mapped is null)
        {
            var companies = await docs.ListCompaniesAsync(ct).ConfigureAwait(false);
            if (!companies.Ok)
            {
                return new PublishResultJson(destination, false, Error: companies.Error!.ToString(), Kind: companies.Error.Kind.ToString().ToLowerInvariant(), Retryable: companies.Error.Retryable);
            }

            var match = CompanyMapper.Match(bundle.Company, companies.Value!);
            if (match is { Confidence: MatchConfidence.Exact })
            {
                mapped = await mappings.RememberAsync(bundle.Company, match.Company, "exact", ct).ConfigureAwait(false);
            }
            else
            {
                var hint = match is null
                    ? "Map it in Settings → Integrations, then publish the article again."
                    : $"It may be \"{match.Company.Name}\"; confirm the mapping in Settings → Integrations, then publish the article again.";
                return new PublishResultJson(destination, false, Error: $"\"{bundle.Company}\" is not mapped to a company in the documentation platform. {hint}", Kind: "needs_mapping");
            }
        }

        var article = new KbArticle(
            mapped.DocCompanyId,
            bundle.Note.SuggestedTitle,
            KbArticleFormatter.Html(bundle.Note, bundle.TicketId, bundle.Frames.Count),
            [.. bundle.Frames.Select((frame, i) => new NoteAttachment($"screenshot-{i + 1}{Extension(frame.MediaType)}", frame.MediaType, Convert.FromBase64String(frame.Image)))]);
        var result = await docs.PublishArticleAsync(article, ct).ConfigureAwait(false);
        return result.Ok
            ? new PublishResultJson(destination, true, result.Value!.Id, result.Value.Url)
            : new PublishResultJson(destination, false, Error: result.Error!.ToString(), Kind: result.Error.Kind.ToString().ToLowerInvariant(), Retryable: result.Error.Retryable);
    }

    private static PublishResultJson Answer(string destination, ProviderResult<PublishedNote> result) => result.Ok
        ? new PublishResultJson(destination, true, result.Value!.Id, result.Value.Url)
        : new PublishResultJson(destination, false, Error: result.Error!.ToString(), Kind: result.Error.Kind.ToString().ToLowerInvariant(), Retryable: result.Error.Retryable);

    private static string Extension(string mediaType) => mediaType == "image/png" ? ".png" : ".jpg";
}
