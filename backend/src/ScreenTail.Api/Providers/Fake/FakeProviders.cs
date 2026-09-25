using System.Globalization;

namespace ScreenTail.Api.Providers.Fake;

/// <summary>
/// A PSA that behaves, and misbehaves on request (ST-090).
///
/// It exists for two jobs. It is what the contract harness runs against, so the rules every provider has
/// to follow are executable before the first real one is written. And it is what the client's publish
/// path develops against, so ST-078's Review screen can be finished without a ConnectWise sandbox.
///
/// <b>Not registered in production.</b> `ProvidersAreNeverRealInProduction` fails the build if it is: a
/// fake PSA in a deployment would tell a technician their note was published when nothing was.
/// </summary>
public sealed class FakePsaProvider : IPsaProvider
{
    private readonly List<TicketRef> _tickets =
    [
        new("48213", "Printer offline in reception", "Acme Dental", "In Progress"),
        new("48219", "Outlook will not open after update", "Acme Dental", "New"),
        new("50011", "New starter setup", "Borough Legal", "In Progress"),
    ];

    /// <summary>Set to make every call fail this way. For the contract harness and for ST-073's failure UX.</summary>
    public ProviderError? Fail { get; set; }

    public List<TicketNote> Notes { get; } = [];

    public List<TimeEntry> TimeEntries { get; } = [];

    public string Name => "fake-psa";

    public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(Fail is { } error ? ProviderResult.Failure<bool>(error) : ProviderResult.Success(true));

    public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        if (Fail is { } error)
        {
            return Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(error));
        }

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3)
        {
            // Spec §5 S3 searches from three characters. Refusing here rather than returning everything
            // keeps a provider from being asked for its whole ticket list by a stray keystroke.
            return Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(new ProviderError(
                ProviderErrorKind.Invalid,
                "The search needs at least three characters.",
                "Type a little more of the ticket number or summary.")));
        }

        IReadOnlyList<TicketRef> found =
        [
            .. _tickets.Where(t =>
                t.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Company.Contains(query, StringComparison.OrdinalIgnoreCase)),
        ];

        return Task.FromResult(ProviderResult.Success(found));
    }

    public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) =>
        Task.FromResult(Fail is { } error
            ? ProviderResult.Failure<IReadOnlyList<TicketRef>>(error)
            : ProviderResult.Success<IReadOnlyList<TicketRef>>([.. _tickets.Take(10)]));

    public Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (Fail is { } error)
        {
            return Task.FromResult(ProviderResult.Failure<PublishedNote>(error));
        }

        if (!_tickets.Exists(t => t.Id == note.TicketId))
        {
            return Task.FromResult(ProviderResult.Failure<PublishedNote>(new ProviderError(
                ProviderErrorKind.NotFound,
                $"Ticket {note.TicketId} is not in the PSA.",
                "Pick the ticket again in Review.")));
        }

        Notes.Add(note);
        return Task.FromResult(ProviderResult.Success(
            new PublishedNote(Notes.Count.ToString(CultureInfo.InvariantCulture), null)));
    }

    public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Fail is { } error)
        {
            return Task.FromResult(ProviderResult.Failure<PublishedNote>(error));
        }

        if (entry.Minutes <= 0)
        {
            return Task.FromResult(ProviderResult.Failure<PublishedNote>(new ProviderError(
                ProviderErrorKind.Invalid,
                "A time entry needs at least one minute.",
                "Check the suggested duration in Review.")));
        }

        TimeEntries.Add(entry);
        return Task.FromResult(ProviderResult.Success(
            new PublishedNote(TimeEntries.Count.ToString(CultureInfo.InvariantCulture), null)));
    }
}

/// <summary>A documentation platform that behaves, and misbehaves on request (ST-090).</summary>
public sealed class FakeDocProvider : IDocProvider
{
    private readonly List<CompanyRef> _companies =
    [
        new("c-1", "Acme Dental"),
        new("c-2", "Borough Legal"),
    ];

    public ProviderError? Fail { get; set; }

    public List<KbArticle> Articles { get; } = [];

    public string Name => "fake-docs";

    public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(Fail is { } error ? ProviderResult.Failure<bool>(error) : ProviderResult.Success(true));

    public Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default) =>
        Task.FromResult(Fail is { } error
            ? ProviderResult.Failure<IReadOnlyList<CompanyRef>>(error)
            : ProviderResult.Success<IReadOnlyList<CompanyRef>>([.. _companies]));

    public Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (Fail is { } error)
        {
            return Task.FromResult(ProviderResult.Failure<PublishedArticle>(error));
        }

        if (!_companies.Exists(c => c.Id == article.CompanyId))
        {
            // Publishing to the wrong company is a customer's runbook in another customer's knowledge
            // base, so an unknown company is refused rather than guessed at.
            return Task.FromResult(ProviderResult.Failure<PublishedArticle>(new ProviderError(
                ProviderErrorKind.NotFound,
                $"Company {article.CompanyId} is not in the documentation platform.",
                "Map the company in Settings → Integrations.")));
        }

        Articles.Add(article);
        return Task.FromResult(ProviderResult.Success(
            new PublishedArticle(Articles.Count.ToString(CultureInfo.InvariantCulture), null)));
    }
}
