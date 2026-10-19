using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Publish;

/// <summary>
/// Sends each destination to the tenant's PSA and answers each on its own (ST-093, ST-094). A failure in
/// one is not a failure of the others: the note that landed stays landed, and the response says exactly
/// which part did not, so the client's Retry sends only that. INV-3: this runs because a technician
/// pressed Publish, and for no other reason.
///
/// The destinations go at the same time, under the publish's own deadline (2026-10-12 review, S-68 and
/// E-42). One after another, a slow time entry held the note back, and the request's timeout answered
/// with an empty 504 that threw away the destinations that had landed, which Retry then sent again.
/// The deadline counts from when the request arrived and ends before the request's timeout, so the
/// answer always carries every destination: what landed, what was never sent, and for what was sent
/// and never confirmed, that it may have landed.
/// </summary>
public static partial class Publisher
{
    public const string Partial = "partial";
    public const string TimedOut = "timed_out";

    /// <summary>It was sent, and what came back could not be read, so it may have landed. Not retryable, like <see cref="TimedOut"/>.</summary>
    public const string Unconfirmed = "unconfirmed";

    /// <param name="docs">The tenant's documentation platform, or null when none is connected: the KB destination then says so and the rest still goes.</param>
    /// <param name="mappings">Where a PSA company's Hudu company is remembered (ST-097). Null in tests that have no KB destination.</param>
    /// <param name="options">The publish deadline. Defaults when null.</param>
    /// <param name="spent">How much of the deadline had gone before this was called: the request's arrival, its body and the lookups before it.</param>
    /// <param name="log">Where a destination that ended in an exception is noted, by the exception's type and nothing it carried.</param>
    /// <param name="ct">The request's own token. When it ends, the caller has gone and nothing is answered.</param>
    public static async Task<PublishResponse> PublishAsync(
        IPsaProvider psa,
        PublishBundle bundle,
        IDocProvider? docs = null,
        CompanyMappings? mappings = null,
        PublishOptions? options = null,
        TimeProvider? time = null,
        TimeSpan spent = default,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(psa);
        ArgumentNullException.ThrowIfNull(bundle);
        options ??= new PublishOptions();
        var destinations = bundle.Destinations.Distinct(StringComparer.Ordinal).ToList();
        var text = NoteFormatter.Render(bundle.Note, [.. bundle.Frames.Select(f => f.Id)], bundle.Reviewer, bundle.Footer);

        // Decoded once, and before anything is sent: the note and the article share the bytes, and a
        // frame that cannot be decoded is found here rather than after the time entry has landed.
        IReadOnlyList<NoteAttachment> attachments = destinations.Contains(Destinations.TicketNote) || destinations.Contains(Destinations.KbArticle)
            ? [.. bundle.Frames.Select((frame, i) => new NoteAttachment($"screenshot-{i + 1}{Extension(frame.MediaType)}", frame.MediaType, Convert.FromBase64String(frame.Image)))]
            : [];

        // A body that took the whole deadline to arrive leaves nothing to send with: the timer is spent
        // already, and each destination says it was never sent rather than that it may have landed.
        var left = options.Deadline - spent;
        using var timer = left > TimeSpan.Zero ? new CancellationTokenSource(left, time ?? TimeProvider.System) : new CancellationTokenSource();
        if (left <= TimeSpan.Zero)
        {
            await timer.CancelAsync().ConfigureAwait(false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var results = await Task.WhenAll(destinations.Select(destination => AnswerAsync(destination, psa, bundle, text, attachments, docs, mappings, log, deadline.Token, ct))).ConfigureAwait(false);
        return new PublishResponse(results);
    }

    private static async Task<PublishResultJson> AnswerAsync(
        string destination,
        IPsaProvider psa,
        PublishBundle bundle,
        string text,
        IReadOnlyList<NoteAttachment> attachments,
        IDocProvider? docs,
        CompanyMappings? mappings,
        ILogger? log,
        CancellationToken deadline,
        CancellationToken request)
    {
        var sent = new Sending();
        try
        {
            return destination switch
            {
                Destinations.TicketNote => Answer(destination, await psa.AddNoteAsync(
                    new TicketNote(bundle.TicketId, text, Internal: bundle.NoteType == "internal", attachments),
                    sent.From(deadline)).ConfigureAwait(false)),

                Destinations.TimeEntry => Answer(destination, await psa.AddTimeEntryAsync(
                    new TimeEntry(bundle.TicketId, bundle.StartedAt ?? DateTimeOffset.UtcNow, bundle.Minutes, text, bundle.Billable, Internal: bundle.NoteType == "internal"),
                    sent.From(deadline)).ConfigureAwait(false)),

                // Said per destination rather than refusing the whole publish, so the note and the time
                // still go whatever the knowledge base thinks.
                Destinations.KbArticle => docs is null || mappings is null
                    ? new PublishResultJson(destination, false, Error: "No documentation platform is connected, so no article was written. The note and the time entry are unaffected.", Kind: "not_configured")
                    : await ArticleAsync(destination, docs, mappings, bundle, attachments, sent, deadline).ConfigureAwait(false),

                _ => new PublishResultJson(destination, false, Error: "That is not a destination.", Kind: "invalid"),
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !request.IsCancellationRequested)
        {
            // Once the call was on its way and had not answered, it may have landed. Not retryable: the
            // technician looks before sending it again, which is the one way to avoid a second copy.
            return sent.Started
                ? new PublishResultJson(destination, false, Error: OutOfTime(destination), Kind: TimedOut)
                : NeverSent(destination, $"The publish ran out of time before the {Name(destination)} was sent");
        }
        catch (Exception ex) when (!request.IsCancellationRequested)
        {
            // An answer that could not be read, or a fault of our own. The destinations run together, so
            // letting it escape would turn every other destination's answer, including one that landed,
            // into a 500 that Retry answers by sending them all again. The type only: an exception's
            // message can quote what the provider sent back.
            var answer = sent.Started
                ? new PublishResultJson(destination, false, Error: CouldNotRead(destination), Kind: Unconfirmed)
                : NeverSent(destination, $"ScreenTail could not get the {Name(destination)} ready to send");
            if (log is not null)
            {
                Ended(log, destination, ex.GetType().Name, answer.Kind!);
            }

            return answer;
        }
        catch (Exception) when (!request.IsCancellationRequested)
        {
            // Providers return their failures as values; this is the backstop for the bug that still
            // throws (2026-10-12 review). Answered on its own, so the destinations already written
            // stay written and are reported, rather than one 500 telling the client nothing went.
            // The exception's own words can quote what it failed on and are not passed on.
            return Unconfirmed(destination);
        }
    }

    /// <summary>A destination whose outcome nobody can state. Not retryable: a second send may be a second note.</summary>
    private static PublishResultJson Unconfirmed(string destination)
    {
        var (what, where) = destination switch
        {
            Destinations.TimeEntry => ("time entry", "the ticket"),
            Destinations.KbArticle => ("article", "the documentation platform"),
            _ => ("note", "the ticket"),
        };
        return new PublishResultJson(
            destination,
            false,
            Error: $"ScreenTail could not tell whether the {what} was saved. Check {where} before publishing it again, so nothing is added twice.",
            Kind: Kind(ProviderErrorKind.Unknown),
            Retryable: false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publish destination {Destination} ended with {ExceptionType}; answered as {Kind}")]
    private static partial void Ended(ILogger log, string destination, string exceptionType, string kind);

    /// <summary>
    /// Whether a destination's call has been made. Until then, a deadline or a fault means nothing was
    /// sent and it may simply go again; from then on, it may have landed.
    /// </summary>
    private sealed class Sending
    {
        public bool Started { get; private set; }

        /// <summary>The token to send with, once the deadline has been checked; from here on it counts as sent.</summary>
        public CancellationToken From(CancellationToken deadline)
        {
            deadline.ThrowIfCancellationRequested();
            Started = true;
            return deadline;
        }
    }

    private static string Name(string destination) => destination switch
    {
        Destinations.TicketNote => "note",
        Destinations.TimeEntry => "time entry",
        _ => "article",
    };

    /// <summary>Nothing left this service for it, so trying again is safe and is the advice.</summary>
    private static PublishResultJson NeverSent(string destination, string why) => new(
        destination,
        false,
        Error: destination == Destinations.KbArticle
            ? $"{why}, so nothing was written. Publish the article again."
            : $"{why}, so nothing was sent. Publish the {Name(destination)} again.",
        Kind: "unavailable",
        Retryable: true);

    private static string OutOfTime(string destination) => destination switch
    {
        Destinations.TicketNote => "The PSA had not answered when the publish ran out of time, so the note may already be on the ticket. Check the ticket before you publish the note again.",
        Destinations.TimeEntry => "The PSA had not answered when the publish ran out of time, so the time entry may already be on the ticket. Check the ticket before you publish the time entry again.",
        _ => "The documentation platform had not answered when the publish ran out of time, so the article may already be written. Check the knowledge base before you publish the article again.",
    };

    private static string CouldNotRead(string destination) => destination switch
    {
        Destinations.TicketNote => "The PSA's answer to the note could not be read, so the note may already be on the ticket. Check the ticket before you publish the note again.",
        Destinations.TimeEntry => "The PSA's answer to the time entry could not be read, so the time entry may already be on the ticket. Check the ticket before you publish the time entry again.",
        _ => "The documentation platform's answer could not be read, so the article may already be written. Check the knowledge base before you publish the article again.",
    };

    /// <summary>
    /// The article: the ticket's company mapped to the platform's — remembered, or exact and then
    /// remembered — or a mapping asked for. A likely match is offered in the message and never assumed:
    /// the wrong company is a customer's runbook in another customer's knowledge base.
    /// </summary>
    private static async Task<PublishResultJson> ArticleAsync(string destination, IDocProvider docs, CompanyMappings mappings, PublishBundle bundle, IReadOnlyList<NoteAttachment> attachments, Sending sent, CancellationToken ct)
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
                return Failed(destination, companies.Error!);
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
            attachments);
        var result = await docs.PublishArticleAsync(article, sent.From(ct)).ConfigureAwait(false);
        return result.Ok
            ? Landed(destination, result.Value!.Id, result.Value.Url, result.Value.Incomplete)
            : Failed(destination, result.Error!);
    }

    private static PublishResultJson Answer(string destination, ProviderResult<PublishedNote> result) => result.Ok
        ? Landed(destination, result.Value!.Id, result.Value.Url, result.Value.Incomplete)
        : Failed(destination, result.Error!);

    /// <summary>
    /// It is there. When something that goes with it is not, the answer is still ok, with its id, and
    /// <c>partial</c> says the words in <c>error</c> are about what is missing: the client's Retry sends
    /// every destination that is not ok, so anything else would post it twice (S-69, E-39).
    /// </summary>
    private static PublishResultJson Landed(string destination, string id, Uri? link, ProviderError? incomplete) => incomplete is null
        ? new PublishResultJson(destination, true, id, link)
        : new PublishResultJson(destination, true, id, link, Error: incomplete.ToString(), Kind: Partial);

    private static PublishResultJson Failed(string destination, ProviderError error) =>
        new(destination, false, Error: error.ToString(), Kind: error.Kind.ToString().ToLowerInvariant(), Retryable: error.Retryable);

    private static string Kind(ProviderErrorKind kind) => kind.ToString().ToLowerInvariant();

    private static string Extension(string mediaType) => mediaType == "image/png" ? ".png" : ".jpg";
}
