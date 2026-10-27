using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;

namespace ScreenTail.Api.Providers.Demo;

/// <summary>
/// The demo PSA's switch (ST-090): <c>Providers:Demo</c>. Off unless said otherwise.
///
/// With it on, the tenant's PSA is <see cref="DemoPsaProvider"/> and its knowledge base is
/// <see cref="DemoDocProvider"/>, for every tenant, so a live demonstration can end with a drafted note on a
/// ticket while there is no ConnectWise sandbox. <b>It is refused outside Development</b>: the service does
/// not start with it, because a deployment that has it on would tell a technician their note was published
/// when it went nowhere a customer or a colleague could ever read it.
/// </summary>
public sealed class DemoOptions
{
    public const string Section = "Providers";

    /// <summary>The setting's full name, for messages and documentation.</summary>
    public const string Setting = Section + ":" + nameof(Demo);

    public bool Demo { get; set; }

    /// <summary>Throws when the switch is on anywhere but Development.</summary>
    public void Validate(bool isDevelopment)
    {
        if (Demo && !isDevelopment)
        {
            throw new InvalidOperationException(
                $"{Setting} is on outside Development. It replaces the tenant's PSA and knowledge base with an "
                + "in-memory demo that no customer or colleague can see, so the service refuses to start with it. "
                + "Remove the setting.");
        }
    }
}

/// <summary>The names the demo goes by. Never a real product's: an audience must not take it for ConnectWise or Hudu.</summary>
public static class DemoNames
{
    /// <summary>The integrations list's provider name for the demo PSA. Reserved: no credential may be stored under it.</summary>
    public const string Psa = "demo-psa";

    /// <summary>The integrations list's provider name for the demo knowledge base. Reserved the same way.</summary>
    public const string Kb = "demo-kb";

    public const string PsaTitle = "Demo PSA (not ConnectWise)";

    public const string KbTitle = "Demo knowledge base (not Hudu)";

    /// <summary>What the integrations list says in place of a site: where the demo's tickets live.</summary>
    public const string Where = "In memory on this backend, Development only";

    /// <summary>Any provider name in the demo's namespace, which a tenant may not use for a real one.</summary>
    public static bool IsReserved(string provider) =>
        provider is not null && provider.StartsWith("demo-", StringComparison.Ordinal);
}

/// <param name="Screenshots">How many screenshots came with it. The images themselves are not kept (INV-7).</param>
public sealed record DemoNote(string Id, DateTimeOffset At, bool Internal, string Body, int Screenshots);

public sealed record DemoTimeEntry(string Id, DateTimeOffset At, DateTimeOffset StartedAt, int Minutes, bool Billable, bool Internal);

/// <param name="Screenshots">How many screenshots came with it. The images themselves are not kept (INV-7).</param>
public sealed record DemoArticle(string Id, DateTimeOffset At, string CompanyId, string Company, string Title, bool Draft, int Screenshots);

/// <summary>A demo ticket as the read endpoint shows it: what the PSA search shows, and everything published to it.</summary>
public sealed record DemoTicket(TicketRef Ticket, IReadOnlyList<DemoNote> Notes, IReadOnlyList<DemoTimeEntry> TimeEntries);

/// <summary>
/// The demo's tickets, companies and everything published to them, for the life of the process and no
/// longer. One for the whole backend: the demo is a laptop's, and every tenant on it sees the same tickets.
///
/// Notes and articles are kept as text with a count of their screenshots. The screenshot bytes are not
/// kept: the backend holds frames for one request only (INV-7), and a demo is no reason to change that.
/// </summary>
public sealed class DemoStore(TimeProvider time)
{
    private readonly Lock _gate = new();

    // A dental practice's printer is the demo's story; the rest are there so a search has something to
    // narrow down and the recent list looks like a working day.
    private readonly List<Entry> _tickets =
    [
        new(new TicketRef("48213", "Printer offline in reception", "Acme Dental", "In Progress")),
        new(new TicketRef("48219", "Outlook will not open after update", "Acme Dental", "New")),
        new(new TicketRef("48224", "Front desk scanner not saving to the shared folder", "Acme Dental", "New")),
        new(new TicketRef("48230", "X-ray workstation slow to sign in", "Acme Dental", "Scheduled")),
        new(new TicketRef("50011", "New starter setup", "Borough Legal", "In Progress")),
        new(new TicketRef("50017", "VPN drops every twenty minutes", "Borough Legal", "In Progress")),
        new(new TicketRef("50023", "Shared mailbox missing from Outlook", "Harbour Vets", "New")),
    ];

    // The same names as the tickets' companies, so an article is filed without a mapping prompt.
    private readonly List<CompanyRef> _companies =
    [
        new("demo-kb-1", "Acme Dental"),
        new("demo-kb-2", "Borough Legal"),
        new("demo-kb-3", "Harbour Vets"),
    ];

    private readonly List<DemoArticle> _articles = [];
    private int _next;

    public IReadOnlyList<TicketRef> Tickets()
    {
        lock (_gate)
        {
            return [.. _tickets.Select(t => t.Ticket)];
        }
    }

    /// <summary>The open tickets most recently published to first, then in the order they were seeded.</summary>
    public IReadOnlyList<TicketRef> Recent(int count)
    {
        lock (_gate)
        {
            return [.. _tickets
                .Select((t, i) => (t, i))
                .OrderByDescending(x => x.t.Touched ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.i)
                .Take(count)
                .Select(x => x.t.Ticket)];
        }
    }

    public DemoTicket? Find(string id)
    {
        lock (_gate)
        {
            return _tickets.Find(t => t.Ticket.Id == id) is { } entry
                ? new DemoTicket(entry.Ticket, [.. entry.Notes], [.. entry.Times])
                : null;
        }
    }

    public IReadOnlyList<CompanyRef> Companies()
    {
        lock (_gate)
        {
            return [.. _companies];
        }
    }

    public IReadOnlyList<DemoArticle> Articles()
    {
        lock (_gate)
        {
            return [.. _articles];
        }
    }

    /// <summary>The note, or null when there is no such ticket.</summary>
    public DemoNote? AddNote(TicketNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        lock (_gate)
        {
            if (_tickets.Find(t => t.Ticket.Id == note.TicketId) is not { } entry)
            {
                return null;
            }

            var at = time.GetUtcNow();
            var added = new DemoNote(NextId("note"), at, note.Internal, note.Body, note.Attachments.Count);
            entry.Notes.Add(added);
            entry.Touched = at;
            return added;
        }
    }

    /// <summary>The entry, or null when there is no such ticket.</summary>
    public DemoTimeEntry? AddTimeEntry(TimeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_tickets.Find(t => t.Ticket.Id == entry.TicketId) is not { } ticket)
            {
                return null;
            }

            var at = time.GetUtcNow();
            var added = new DemoTimeEntry(NextId("time"), at, entry.StartedAt, entry.Minutes, entry.Billable, entry.Internal);
            ticket.Times.Add(added);
            ticket.Touched = at;
            return added;
        }
    }

    /// <summary>The article, or null when there is no such company.</summary>
    public DemoArticle? AddArticle(KbArticle article)
    {
        ArgumentNullException.ThrowIfNull(article);
        lock (_gate)
        {
            if (_companies.Find(c => c.Id == article.CompanyId) is not { } company)
            {
                return null;
            }

            var added = new DemoArticle(NextId("kb"), time.GetUtcNow(), company.Id, company.Name, article.Title, article.Draft, article.Attachments.Count);
            _articles.Add(added);
            return added;
        }
    }

    private string NextId(string kind) => $"demo-{kind}-{++_next:D4}";

    private sealed class Entry(TicketRef ticket)
    {
        public TicketRef Ticket { get; } = ticket;

        public List<DemoNote> Notes { get; } = [];

        public List<DemoTimeEntry> Times { get; } = [];

        public DateTimeOffset? Touched { get; set; }
    }
}

/// <summary>
/// A PSA that keeps what is published to it in <see cref="DemoStore"/> (ST-090), for a live demonstration
/// without a ConnectWise sandbox. It follows the same rules as a real one: three characters or a number to
/// search, a missing ticket is <see cref="ProviderErrorKind.NotFound"/>, a time entry needs a minute.
///
/// A published note comes back with an id and no link. The client labels a link "Open in ConnectWise",
/// and a demo must not say that.
/// </summary>
public sealed class DemoPsaProvider(DemoStore store) : IPsaProvider
{
    public string Name => DemoNames.Psa;

    public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Success(true));

    public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        var wanted = (query ?? string.Empty).Trim();
        if (wanted.Length == 0 || (wanted.Length < 3 && !wanted.All(char.IsAsciiDigit)))
        {
            return Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(new ProviderError(
                ProviderErrorKind.Invalid,
                "The search needs at least three characters.",
                "Type a little more of the ticket number or summary.")));
        }

        IReadOnlyList<TicketRef> found =
        [
            .. store.Tickets().Where(t =>
                t.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || t.Summary.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || t.Company.Contains(wanted, StringComparison.OrdinalIgnoreCase)),
        ];
        return Task.FromResult(ProviderResult.Success(found));
    }

    public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Success(store.Recent(10)));

    public Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        return Task.FromResult(store.AddNote(note) is { } added
            ? ProviderResult.Success(new PublishedNote(added.Id, null))
            : ProviderResult.Failure<PublishedNote>(Missing(note.TicketId)));
    }

    public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Minutes <= 0)
        {
            return Task.FromResult(ProviderResult.Failure<PublishedNote>(new ProviderError(
                ProviderErrorKind.Invalid,
                "A time entry needs at least one minute.",
                "Check the suggested duration in Review.")));
        }

        return Task.FromResult(store.AddTimeEntry(entry) is { } added
            ? ProviderResult.Success(new PublishedNote(added.Id, null))
            : ProviderResult.Failure<PublishedNote>(Missing(entry.TicketId)));
    }

    private static ProviderError Missing(string ticketId) => new(
        ProviderErrorKind.NotFound,
        string.Create(CultureInfo.InvariantCulture, $"Ticket {ticketId} is not in the demo PSA."),
        "Pick the ticket again in Review.");
}

/// <summary>A knowledge base that keeps what is published to it in <see cref="DemoStore"/> (ST-090). Its companies have the demo tickets' names, so nothing needs mapping.</summary>
public sealed class DemoDocProvider(DemoStore store) : IDocProvider
{
    public string Name => DemoNames.Kb;

    public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Success(true));

    public Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Success(store.Companies()));

    public Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(article);
        return Task.FromResult(store.AddArticle(article) is { } added
            ? ProviderResult.Success(new PublishedArticle(added.Id, null))
            : ProviderResult.Failure<PublishedArticle>(new ProviderError(
                ProviderErrorKind.NotFound,
                string.Create(CultureInfo.InvariantCulture, $"Company {article.CompanyId} is not in the demo knowledge base."),
                "Pick a ticket for one of the demo companies.")));
    }
}

/// <summary>The demo PSA for every tenant: there is no credential to read, and nothing to connect.</summary>
public sealed class DemoPsaProviderFactory(DemoStore store) : IPsaProviderFactory
{
    public Task<IPsaProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult<IPsaProvider?>(new DemoPsaProvider(store));
}

/// <summary>The demo knowledge base for every tenant.</summary>
public sealed class DemoDocProviderFactory(DemoStore store) : IDocProviderFactory
{
    public Task<IDocProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult<IDocProvider?>(new DemoDocProvider(store));
}

/// <summary>
/// The company mappings a publish uses while the demo is on (review 2026-10-27): none remembered, none
/// written, none forgotten. A tenant's real mappings point into its real knowledge base; held against the
/// demo's companies they look stale and would be deleted, a hand-confirmed one included. So the demo
/// matches the ticket's company against its own list, in memory, for this publish only: an exact name is
/// filed there, anything else is asked about and not remembered.
/// </summary>
public sealed class DemoCompanyMappings : ICompanyMappings
{
    public Task<CompanyMapping?> FindAsync(string psaCompany, CancellationToken ct = default) =>
        Task.FromResult<CompanyMapping?>(null);

    public Task<CompanyMapping> RememberAsync(string psaCompany, CompanyRef company, string confidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(psaCompany);
        ArgumentNullException.ThrowIfNull(company);
        return Task.FromResult(new CompanyMapping
        {
            Id = Guid.Empty,
            PsaCompany = psaCompany.Trim(),
            DocCompanyId = company.Id,
            DocCompanyName = company.Name,
            Confidence = confidence,
        });
    }

    public Task<bool> ForgetIfUnchangedAsync(CompanyMapping stale, CancellationToken ct = default) =>
        Task.FromResult(false);
}

public static class DemoRegistration
{
    /// <summary>
    /// Puts the demo in place of the tenant's PSA and knowledge base. Called from <c>Program.cs</c> only
    /// when <see cref="DemoOptions.Demo"/> is on, which <see cref="DemoOptions.Validate"/> allows only in
    /// Development.
    /// </summary>
    public static IServiceCollection AddDemoProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        _ = services.AddSingleton<DemoStore>();
        _ = services.Replace(ServiceDescriptor.Scoped<IPsaProviderFactory, DemoPsaProviderFactory>());
        _ = services.Replace(ServiceDescriptor.Scoped<IDocProviderFactory, DemoDocProviderFactory>());
        return services;
    }
}
