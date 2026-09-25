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
    public static async Task<PublishResponse> PublishAsync(IPsaProvider psa, PublishBundle bundle, CancellationToken ct = default)
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

                // ST-095 brings the documentation platform. Said per destination rather than refusing
                // the whole publish, so the note and the time still go.
                Destinations.KbArticle => new PublishResultJson(destination, false, Error: "Knowledge-base publishing arrives with ST-095. The note and the time entry are unaffected.", Kind: "not_configured"),

                _ => new PublishResultJson(destination, false, Error: "That is not a destination.", Kind: "invalid"),
            });
        }

        return new PublishResponse(results);
    }

    private static PublishResultJson Answer(string destination, ProviderResult<PublishedNote> result) => result.Ok
        ? new PublishResultJson(destination, true, result.Value!.Id, result.Value.Url)
        : new PublishResultJson(destination, false, Error: result.Error!.ToString(), Kind: result.Error.Kind.ToString().ToLowerInvariant(), Retryable: result.Error.Retryable);

    private static string Extension(string mediaType) => mediaType == "image/png" ? ".png" : ".jpg";
}
