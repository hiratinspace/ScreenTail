using System.Globalization;
using ScreenTail.Core.Intel;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review.Publish;

/// <summary>A ticket the PSA offered for the search, or one inferred from the window (ST-077).</summary>
public sealed record TicketMatch(string Id, string Summary, string Company, bool Suggested = false)
{
    /// <summary>Spec §5 S3's row: <c>#id · summary · company</c>.</summary>
    public string Label => $"#{Id} · {Summary} · {Company}";
}

/// <summary>Internal by default: Discussion notes are customer-visible in ConnectWise (v0.4.1 Q2).</summary>
public enum NoteType
{
    Internal,
    Discussion,
}

public enum Destination
{
    TicketNote,
    TimeEntry,
    KbArticle,
}

/// <summary>Everything the publisher needs, in one object, so a retry can send exactly the part that failed.</summary>
public sealed record PublishRequest(
    string SessionId,
    TicketMatch Ticket,
    NoteType NoteType,
    int Minutes,
    IReadOnlySet<Destination> Destinations,
    DraftNote Note,
    IReadOnlyList<string> FrameIds);

/// <param name="Link">Where it landed, for "Open in ConnectWise". Null when it did not.</param>
/// <param name="Error">The provider's own words, for the partial-publish line. Never content of the note.</param>
/// <param name="Kind">The backend's name for the failure, when it gave one. <c>needs_mapping</c> is the one the pane acts on (ST-097).</param>
public sealed record DestinationResult(Destination Destination, bool Ok, Uri? Link = null, string? Error = null, string? Kind = null)
{
    public const string NeedsMapping = "needs_mapping";
}

/// <summary>One of the documentation platform's companies, for the mapping prompt.</summary>
public sealed record CompanyChoice(string Id, string Name);

/// <summary>
/// The mapping prompt's two calls (ST-097 AC2): the platform's companies to choose from, and the answer,
/// which the backend remembers so the next article for that company is filed without asking.
/// </summary>
public sealed class CompanyMapping(
    Func<CancellationToken, Task<IReadOnlyList<CompanyChoice>>> companies,
    Func<string, string, CancellationToken, Task<bool>> map)
{
    public Func<CancellationToken, Task<IReadOnlyList<CompanyChoice>>> Companies { get; } = companies ?? throw new ArgumentNullException(nameof(companies));

    public Func<string, string, CancellationToken, Task<bool>> Map { get; } = map ?? throw new ArgumentNullException(nameof(map));
}

/// <summary>
/// The right pane's rules (ST-078, Spec §5 S3): whether publishing is allowed and why not, what the time
/// entry defaults to, which destinations are chosen, and what to do when one of them fails.
///
/// The PSA is a pair of delegates. ST-092 supplies the search and ST-093/094 the publish; until then the
/// harness passes fakes and the running application passes nothing, which shows "Connect a PSA to
/// publish" and is the truth. Every decision here is testable without a provider, and stays the same
/// when the real one arrives.
/// </summary>
public sealed class PublishPanel
{
    /// <summary>Two characters match half the tickets in a PSA, and each search is a round trip to it.</summary>
    public const int MinimumQueryLength = 3;

    public const string ConnectPrompt = "Connect a PSA to publish";

    private readonly Session _session;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<TicketMatch>>> _search;
    private readonly Func<PublishRequest, CancellationToken, Task<IReadOnlyList<DestinationResult>>> _publish;
    private readonly TimeEntryOptions _rounding;
    private readonly bool _offline;
    private readonly CompanyMapping? _mapping;
    private readonly Dictionary<Destination, DestinationResult> _results = [];

    /// <param name="integrations">The tenant's connected providers, by name. Empty means nothing can be published.</param>
    /// <param name="search">The PSA's ticket search (ST-092).</param>
    /// <param name="publish">Sends the chosen destinations and says how each one went (ST-093, ST-094, ST-096).</param>
    /// <param name="rounding">The tenant's billing rounding; the draft reports unrounded minutes and this side rounds (STATUS §4).</param>
    /// <param name="mapping">The company-mapping prompt's calls (ST-097). Without them a <c>needs_mapping</c> failure is shown as the words it came with.</param>
    public PublishPanel(
        Session session,
        IReadOnlyList<string> integrations,
        Func<string, CancellationToken, Task<IReadOnlyList<TicketMatch>>> search,
        Func<PublishRequest, CancellationToken, Task<IReadOnlyList<DestinationResult>>> publish,
        TimeEntryOptions? rounding = null,
        bool offline = false,
        CompanyMapping? mapping = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(integrations);
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _rounding = rounding ?? new TimeEntryOptions();
        _offline = offline;
        _mapping = mapping;
        Integrations = integrations;

        var draft = session.Draft;
        Minutes = draft is null ? 0 : TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(draft.SuggestedTimeMinutes), _rounding);
        KbArticle = draft?.KbCandidate ?? false;
        KbReason = draft?.KbReason;
    }

    /// <summary>
    /// A pane with nothing to publish to: "Connect a PSA to publish", and delegates that are never
    /// reached because the pane is blocked before them. What the running application shows until
    /// ST-092 and ST-093 wire the real ones.
    /// </summary>
    public static PublishPanel Unconnected(Session session) => new(
        session,
        [],
        (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
        (_, _) => throw new InvalidOperationException("Publishing arrives with ST-093."));

    public IReadOnlyList<string> Integrations { get; }

    public bool HasIntegrations => Integrations.Count > 0;

    public TicketMatch? Ticket { get; private set; }

    public IReadOnlyList<TicketMatch> Matches { get; private set; } = [];

    public NoteType NoteType { get; set; } = NoteType.Internal;

    /// <summary>Editable. Starts from the draft's active minutes, rounded the tenant's way.</summary>
    public int Minutes
    {
        get;
        set => field = Math.Max(0, value);
    }

    /// <summary>"rounded to 15", beside the field, so the number is not mistaken for the time worked.</summary>
    public string RoundingNote => $"rounded to {_rounding.IncrementMinutes}";

    public bool TicketNote { get; set; } = true;

    public bool TimeEntry { get; set; } = true;

    /// <summary>Follows the draft's <c>kb_candidate</c>; <see cref="KbReason"/> says why in either case.</summary>
    public bool KbArticle { get; set; }

    public string? KbReason { get; }

    public bool IsPublishing { get; private set; }

    public IReadOnlyList<DestinationResult> Results => [.. _results.Values.OrderBy(r => r.Destination)];

    /// <summary>Every chosen destination landed.</summary>
    public bool Published => _results.Count > 0 && _results.Values.All(r => r.Ok);

    /// <summary>Some landed and some did not: the successes are kept and Retry sends the rest.</summary>
    public bool PartiallyPublished => _results.Values.Any(r => r.Ok) && _results.Values.Any(r => !r.Ok);

    public IReadOnlyList<Destination> Failed => [.. _results.Values.Where(r => !r.Ok).Select(r => r.Destination).Order()];

    /// <summary>
    /// The article did not go because the backend has no mapping for the ticket's company, and this pane
    /// can offer one (ST-097 AC2: unmatched → prompt once at publish). A ticket with no company at all is
    /// the same kind of failure with nothing to map, and stays a line in the results.
    /// </summary>
    public bool NeedsMapping =>
        _mapping is not null
        && CompanyToMap is not null
        && _results.TryGetValue(Destination.KbArticle, out var article)
        && article is { Ok: false, Kind: DestinationResult.NeedsMapping };

    /// <summary>The PSA's name for the company, which is what the backend keys the mapping by.</summary>
    public string? CompanyToMap => Ticket?.Company is { Length: > 0 } company ? company : null;

    /// <summary>
    /// Why Publish is disabled, or null when it is not. The wording is Spec §5 S3's and §6's, kept in one
    /// place with <see cref="NoteBanners.PublishBlockedBecause"/> so the note pane and this pane never
    /// disagree about why.
    /// </summary>
    public string? BlockedBecause
    {
        get
        {
            if (!HasIntegrations)
            {
                return ConnectPrompt;
            }

            if (NoteBanners.PublishBlockedBecause(_session, ticketChosen: Ticket is not null, _offline) is { } reason)
            {
                return reason;
            }

            return ChosenDestinations().Count == 0 ? "Choose at least one destination" : null;
        }
    }

    public bool CanPublish => BlockedBecause is null && !IsPublishing;

    /// <summary>Spec §6's toast line for what happened, or null before anything has.</summary>
    public string? Summary
    {
        get
        {
            if (_results.Count == 0)
            {
                return null;
            }

            if (Published)
            {
                var time = _results.TryGetValue(Destination.TimeEntry, out var entry) && entry.Ok
                    ? $" · {Minutes / 60}:{Minutes % 60:00} logged"
                    : string.Empty;
                return $"Published to ticket #{Ticket?.Id}{time}";
            }

            var failed = _results.Values.Where(r => !r.Ok).ToList();
            var landed = _results.Values.Where(r => r.Ok).Select(r => Name(r.Destination)).ToList();
            var first = failed[0];
            var prefix = landed.Count > 0 ? $"{Capitalise(landed[0])} published. " : string.Empty;
            return $"{prefix}{Capitalise(Name(first.Destination))} failed: {first.Error ?? "no reason given"}";
        }
    }

    /// <summary>Asks the PSA once the query is worth asking about. Returns whether it asked.</summary>
    public async Task<bool> SearchAsync(string query, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();

        // A number is an id and worth asking about at once (ST-092: numeric means exact id first).
        if (trimmed.Length < MinimumQueryLength && !trimmed.All(char.IsAsciiDigit))
        {
            Matches = [];
            return false;
        }

        if (trimmed.Length == 0)
        {
            Matches = [];
            return false;
        }

        Matches = await _search(trimmed, ct).ConfigureAwait(false);
        return true;
    }

    public void Choose(TicketMatch? match) => Ticket = match;

    /// <summary>ST-077: pre-selected from the window title, and badged so the technician checks it.</summary>
    public void Suggest(TicketMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        Ticket = match with { Suggested = true };
    }

    /// <summary>
    /// Publishes every chosen destination. Refused rather than attempted when blocked: the button is
    /// disabled for a reason, and a shortcut must not get past it.
    /// </summary>
    public Task<IReadOnlyList<DestinationResult>> PublishAsync(DraftNote note, IReadOnlyList<string> frameIds, CancellationToken ct = default)
    {
        if (BlockedBecause is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        return SendAsync(ChosenDestinations(), note, frameIds, ct);
    }

    /// <summary>Sends only what failed. The note that landed is not sent again (ST-094 AC3).</summary>
    public Task<IReadOnlyList<DestinationResult>> RetryAsync(DraftNote note, IReadOnlyList<string> frameIds, CancellationToken ct = default)
    {
        var failed = Failed.ToHashSet();
        if (failed.Count == 0)
        {
            return Task.FromResult(Results);
        }

        return SendAsync(failed, note, frameIds, ct);
    }

    /// <summary>The platform's companies, for the prompt. Empty when there is no prompt to fill.</summary>
    public Task<IReadOnlyList<CompanyChoice>> CompanyChoicesAsync(CancellationToken ct = default) =>
        _mapping is null ? Task.FromResult<IReadOnlyList<CompanyChoice>>([]) : _mapping.Companies(ct);

    /// <summary>
    /// The prompt's answer: remember that the ticket's company is this platform company, then send the
    /// article again — only the article; whatever landed stays landed. Returns whether the backend took
    /// the mapping; when it did not, the prompt stays up with the results as they were.
    /// </summary>
    public async Task<bool> MapAndRetryAsync(string docCompanyId, DraftNote note, IReadOnlyList<string> frameIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docCompanyId);
        if (_mapping is null || CompanyToMap is not { } company)
        {
            return false;
        }

        if (!await _mapping.Map(company, docCompanyId, ct).ConfigureAwait(false))
        {
            return false;
        }

        _ = await RetryAsync(note, frameIds, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<IReadOnlyList<DestinationResult>> SendAsync(IReadOnlySet<Destination> destinations, DraftNote note, IReadOnlyList<string> frameIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(frameIds);
        if (IsPublishing)
        {
            throw new InvalidOperationException("A publish is already in flight.");
        }

        IsPublishing = true;
        try
        {
            var results = await _publish(new PublishRequest(_session.SessionId, Ticket!, NoteType, Minutes, destinations, note, frameIds), ct).ConfigureAwait(false);
            foreach (var result in results)
            {
                _results[result.Destination] = result;
            }

            return Results;
        }
        finally
        {
            IsPublishing = false;
        }
    }

    private HashSet<Destination> ChosenDestinations()
    {
        var chosen = new HashSet<Destination>();
        if (TicketNote)
        {
            chosen.Add(Destination.TicketNote);
        }

        if (TimeEntry)
        {
            chosen.Add(Destination.TimeEntry);
        }

        if (KbArticle)
        {
            chosen.Add(Destination.KbArticle);
        }

        return chosen;
    }

    private static string Name(Destination destination) => destination switch
    {
        Destination.TicketNote => "note",
        Destination.TimeEntry => "time entry",
        Destination.KbArticle => "KB article",
        _ => destination.ToString().ToLower(CultureInfo.InvariantCulture),
    };

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
