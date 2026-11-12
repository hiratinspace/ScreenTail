using System.Globalization;
using ScreenTail.Core.Intel;
using ScreenTail.Core.Settings;
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
/// <param name="PublishAgain">
/// The pane knows this session may already be on the ticket and means to send it anyway: a Retry after a
/// landing, which sends only what failed, or a publish after the technician confirmed they checked the
/// ticket. The service refuses a session already marked published without it.
/// </param>
public sealed record PublishRequest(
    string SessionId,
    TicketMatch Ticket,
    NoteType NoteType,
    int Minutes,
    IReadOnlySet<Destination> Destinations,
    DraftNote Note,
    IReadOnlyList<string> FrameIds,
    bool PublishAgain = false);

/// <param name="Link">Where it landed, for "Open in ConnectWise". Null when it did not.</param>
/// <param name="Error">The provider's own words, for the partial-publish line, or for <see cref="Partial"/> what did not attach. Never content of the note.</param>
/// <param name="Kind">The backend's name for the failure, when it gave one. <c>needs_mapping</c> is the one the pane acts on (ST-097).</param>
public sealed record DestinationResult(Destination Destination, bool Ok, Uri? Link = null, string? Error = null, string? Kind = null)
{
    public const string NeedsMapping = "needs_mapping";

    /// <summary>It landed and something with it did not, usually a screenshot. <see cref="Ok"/> is true, so Retry leaves it alone; <see cref="Error"/> says what is missing.</summary>
    public const string Partial = "partial";

    /// <summary>
    /// It was sent and nobody can say whether it landed: the provider's answer could not be read, or the
    /// pipe to the service dropped before the service answered (<see cref="PipePublisher"/>), or the
    /// service refused a session it has already published.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>The backend's publish deadline passed with this destination on its way to the provider.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>
    /// The service refused a session it has already marked published (<see cref="PublishCommands.AlreadyPublished"/>).
    /// The pane's own name, never on the wire: something of it is on the ticket, so it is not "not confirmed".
    /// </summary>
    public const string AlreadyPublished = "already_published";

    /// <summary>
    /// The service refused a session whose last publish may have landed (<see cref="PublishCommands.Unconfirmed"/>,
    /// O2). The pane's own name, never on the wire.
    /// </summary>
    public const string SessionUnconfirmed = "session_unconfirmed";

    /// <summary>
    /// The service, or the pipe to it, refused the whole request with words of its own. The pane's own name,
    /// never on the wire: it says nothing about any destination, so an earlier outcome stands (m-c).
    /// </summary>
    public const string Refused = "refused";

    /// <summary>
    /// Failed, but it may be on the ticket all the same. Sending it again without looking is how a ticket
    /// gets the note twice, so the pane holds every way of sending until the technician says they checked.
    /// </summary>
    public bool MayHaveLanded => !Ok && Kind is Unknown or TimedOut or AlreadyPublished or SessionUnconfirmed;

    /// <summary>The service refused because of an earlier publish, which the pane then reloads and offers per destination (O3).</summary>
    public bool RefusedForAnEarlierPublish => !Ok && Kind is AlreadyPublished or SessionUnconfirmed;
}

/// <summary>How one destination of a publish went, as the result list shows it.</summary>
public enum ResultState
{
    Sent,
    Failed,

    /// <summary>It may be on the ticket: sent, and never confirmed (v0.5.2).</summary>
    NotConfirmed,

    /// <summary>The service refused it because the session is already published.</summary>
    AlreadyPublished,
}

/// <summary>
/// One row of the result list (Spec §5 S3): the destination, a glyph drawn in its state's colour with a
/// word beside it, the link named for the provider it goes to, and the reason when there is one to say
/// here. Worded here, where it is tested without WPF.
/// </summary>
/// <param name="Error">The provider's words, or null: also null when the reason is said elsewhere (the mapping prompt).</param>
/// <param name="LinkText">"Open in ConnectWise", "Open in Hudu": the provider the link goes to, from what is connected.</param>
public sealed record PublishResultRow(Destination Destination, string Label, ResultState State, Uri? Link, string? Error, string LinkText)
{
    public string Mark => State switch
    {
        ResultState.Sent => "✓",
        ResultState.Failed => "✗",
        _ => "?",
    };

    /// <summary>The glyph in words for a screen reader, which would otherwise say "question mark". Unchanged since v0.5.2.</summary>
    public string MarkName => State switch
    {
        ResultState.Sent => "Published",
        ResultState.Failed => "Failed",
        ResultState.NotConfirmed => "Not confirmed",
        _ => "Already published",
    };

    /// <summary>The word beside the glyph, so the state is never a colour alone (Spec §2.1).</summary>
    public string Badge => State switch
    {
        ResultState.Sent => "Sent",
        ResultState.Failed => "Failed",
        ResultState.NotConfirmed => "Not confirmed",
        _ => "Already published",
    };

    /// <summary>The link's name for a screen reader: where it opens and which destination it is.</summary>
    public string LinkName => $"{LinkText}: {Label}";
}

/// <summary>What an earlier publish of the session left on the ticket for one destination (O3).</summary>
public enum EarlierOutcome
{
    Landed,
    Unknown,
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

    /// <summary>Everything chosen landed. Publishing again would post it all a second time.</summary>
    public const string AlreadyPublished = "Already published";

    /// <summary>Some of it landed. Publishing again would post that part a second time; Retry sends the rest.</summary>
    public const string PartlyPublished = "Already partly published. Retry sends only what failed";

    /// <summary>Something may already be on the ticket. Publish, Ctrl+Enter and Retry wait until the technician says they looked.</summary>
    public const string CheckTheTicketFirst = "This may already be on the ticket. Check the ticket, then confirm below before publishing again, so nothing is added twice";

    private readonly Session _session;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<TicketMatch>>> _search;
    private readonly Func<PublishRequest, CancellationToken, Task<IReadOnlyList<DestinationResult>>> _publish;
    private readonly TimeEntryOptions _rounding;
    private readonly bool _offline;

    /// <summary>The draft is on its way from the backend (ST-013), so the reason Publish waits says so.</summary>
    private readonly bool _draftPending;
    private readonly CompanyMapping? _mapping;
    private readonly Dictionary<Destination, DestinationResult> _results = [];

    /// <summary>What earlier publishes left on the ticket, per destination (O3). Absent means never sent, or plainly failed.</summary>
    private readonly Dictionary<Destination, EarlierOutcome> _earlier = [];
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<Destination, EarlierOutcome>>>? _reloadEarlier;

    /// <param name="integrations">The tenant's connected providers, by name. Empty means nothing can be published.</param>
    /// <param name="search">The PSA's ticket search (ST-092). Asked for nothing, it answers the recent tickets.</param>
    /// <param name="publish">Sends the chosen destinations and says how each one went (ST-093, ST-094, ST-096).</param>
    /// <param name="rounding">The tenant's billing rounding; the draft reports unrounded minutes and this side rounds (STATUS §4).</param>
    /// <param name="mapping">The company-mapping prompt's calls (ST-097). Without them a <c>needs_mapping</c> failure is shown as the words it came with.</param>
    /// <param name="earlierOutcomes">What the service remembers an earlier publish left on the ticket (O3): those destinations start unticked.</param>
    /// <param name="reloadEarlier">Asks the service for those again, after it refused a send because of an earlier publish this pane did not know about.</param>
    public PublishPanel(
        Session session,
        IReadOnlyList<string> integrations,
        Func<string, CancellationToken, Task<IReadOnlyList<TicketMatch>>> search,
        Func<PublishRequest, CancellationToken, Task<IReadOnlyList<DestinationResult>>> publish,
        TimeEntryOptions? rounding = null,
        bool offline = false,
        CompanyMapping? mapping = null,
        bool draftPending = false,
        IReadOnlyDictionary<Destination, EarlierOutcome>? earlierOutcomes = null,
        Func<CancellationToken, Task<IReadOnlyDictionary<Destination, EarlierOutcome>>>? reloadEarlier = null)
    {
        _reloadEarlier = reloadEarlier;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(integrations);
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _rounding = rounding ?? new TimeEntryOptions();
        _offline = offline;
        _draftPending = draftPending;
        _mapping = mapping;
        Integrations = integrations;

        var draft = session.Draft;
        Minutes = draft is null ? 0 : TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(draft.SuggestedTimeMinutes), _rounding);
        KbArticle = draft?.KbCandidate ?? false;
        KbReason = draft?.KbReason;
        OfferEarlier(earlierOutcomes);
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

    /// <summary>
    /// Said above Publish when the backend's PSA is its Development demo (ST-090), so nobody watching
    /// takes the note for one on a real ticket. Null for a real PSA, or none.
    /// </summary>
    public string? DemoNotice => Integrations.Any(IntegrationNames.IsDemo)
        ? $"Publishing to {IntegrationNames.Title(IntegrationNames.DemoPsa)}: the backend keeps the note in memory, and it reaches no real ticket."
        : null;

    /// <summary>
    /// The line under the note type. It names ConnectWise, the v1 PSA, except during the demo, when no
    /// note reaches a real PSA and naming one would say otherwise (review 2026-10-27).
    /// </summary>
    public string DiscussionHint => DemoNotice is null
        ? "Discussion notes are visible to the customer in ConnectWise."
        : "Discussion notes would be visible to the customer in a real PSA.";

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

    /// <summary>What an earlier publish left on the ticket for this destination, or null for nothing (never sent, or plainly failed).</summary>
    public EarlierOutcome? EarlierFor(Destination destination) => _earlier.TryGetValue(destination, out var outcome) ? outcome : null;

    /// <summary>An earlier publish of this session left something on the ticket, or may have.</summary>
    public bool HasEarlier => _earlier.Count > 0;

    /// <summary>A chosen destination landed or may have landed before: sending it needs the check.</summary>
    public bool ChosenMayDuplicate => ChosenDestinations().Any(_earlier.ContainsKey);

    public bool IsPublishing { get; private set; }

    public IReadOnlyList<DestinationResult> Results => [.. _results.Values.OrderBy(r => r.Destination)];

    /// <summary>Every chosen destination landed.</summary>
    public bool Published => _results.Count > 0 && _results.Values.All(r => r.Ok);

    /// <summary>Some landed and some did not: the successes are kept and Retry sends the rest.</summary>
    public bool PartiallyPublished => _results.Values.Any(r => r.Ok) && _results.Values.Any(r => !r.Ok);

    public IReadOnlyList<Destination> Failed => [.. _results.Values.Where(r => !r.Ok).Select(r => r.Destination).Order()];

    /// <summary>A destination failed in a way that may still have put it on the ticket (<see cref="DestinationResult.MayHaveLanded"/>).</summary>
    public bool MayHaveLanded => _results.Values.Any(r => r.MayHaveLanded);

    /// <summary>
    /// The technician's "I checked the ticket", which lets the next send go while <see cref="MayHaveLanded"/>.
    /// Spent on that one send: an answer lost a second time needs a second look.
    /// </summary>
    public bool TicketChecked { get; set; }

    /// <summary>
    /// Something Retry would send may be on the ticket: a row that may have landed, or a failed destination
    /// an earlier publish left landed or unknown, which a whole-request refusal since says nothing about
    /// (re-review of #265 at bde6a99).
    /// </summary>
    public bool NeedsTicketCheck => MayHaveLanded || Failed.Any(_earlier.ContainsKey);

    /// <summary>Why Retry is held, or null. Only the unconfirmed check: Retry sends what failed and nothing that landed.</summary>
    public string? RetryBlockedBecause => NeedsTicketCheck && !TicketChecked ? CheckTheTicketFirst : null;

    public bool CanRetry => Failed.Count > 0 && RetryBlockedBecause is null && !IsPublishing;

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
    /// Why the article waits, and what to do, said once, here, beside the choice (ST-097 AC2). The summary
    /// and the row used to say the backend's reason as well. Null when there is no prompt.
    /// </summary>
    public string? MappingPrompt => NeedsMapping
        ? $"“{CompanyToMap}” is not mapped to a company in {DocumentationName} yet. Choose the one it is and the article goes there. The choice is remembered."
        : null;

    /// <summary>The connected provider a destination goes to, or null when none this version knows is connected.</summary>
    public string? ProviderFor(Destination destination) => destination == Destination.KbArticle
        ? Integrations.FirstOrDefault(IntegrationNames.IsDocumentation)
        : Integrations.FirstOrDefault(IntegrationNames.IsPsa);

    /// <summary>"ConnectWise", "Hudu": the provider by the name a sentence uses, or what it is when none is known.</summary>
    public string ProviderName(Destination destination) => ProviderFor(destination) is { } provider
        ? IntegrationNames.InSentence(provider)
        : destination == Destination.KbArticle ? "the knowledge base" : "the PSA";

    private string DocumentationName => ProviderFor(Destination.KbArticle) is { } provider
        ? IntegrationNames.InSentence(provider)
        : "the documentation platform";

    /// <summary>The result list (Spec §5 S3), one row per destination, in the order the form lists them.</summary>
    public IReadOnlyList<PublishResultRow> Rows
    {
        get
        {
            var mapping = NeedsMapping;
            return [.. Results.Select(r => new PublishResultRow(
                r.Destination,
                LabelOf(r.Destination),
                r.Ok ? ResultState.Sent
                    : r.Kind == DestinationResult.AlreadyPublished ? ResultState.AlreadyPublished
                    : r.MayHaveLanded ? ResultState.NotConfirmed
                    : ResultState.Failed,
                r.Link,
                mapping && r.Destination == Destination.KbArticle ? null : r.Error,
                $"Open in {ProviderName(r.Destination)}"))];
        }
    }

    /// <summary>"Retry Hudu": where Retry sends again, which is only what failed (ST-094 AC3).</summary>
    public string RetryLabel => Failed.Count == 0
        ? "Retry"
        : $"Retry {string.Join(" and ", Failed.Select(ProviderName).Distinct(StringComparer.Ordinal))}";

    /// <summary>Retry for a screen reader: each destination it sends again and where.</summary>
    public string RetryName => Failed.Count == 0
        ? "Retry"
        : $"Retry {string.Join(" and ", Failed.Select(d => $"the {Name(d)} in {ProviderName(d)}"))}";

    /// <summary>The destination as the form and the result list name it.</summary>
    public static string LabelOf(Destination destination) => destination switch
    {
        Destination.TicketNote => "Ticket note",
        Destination.TimeEntry => "Time entry",
        Destination.KbArticle => "KB article",
        _ => destination.ToString(),
    };

    /// <summary>
    /// Why Publish is disabled, or null when it is not. The wording is Spec §5 S3's and §6's, kept in one
    /// place with <see cref="NoteBanners.PublishBlockedBecause"/> so the note pane and this pane never
    /// disagree about why.
    /// </summary>
    public string? BlockedBecause
    {
        get
        {
            // First of all: something may be on the ticket already, and nobody has looked. Said before the
            // landed reasons below because it is the one the technician has to act on.
            if (RetryBlockedBecause is { } check)
            {
                return check;
            }

            // Then, and whatever the toggles say now (T-129): once anything has landed, Publish would
            // send it again, and Ctrl+Enter reaches here even while the result list hides the button.
            if (_results.Values.Any(r => r.Ok))
            {
                return Published ? AlreadyPublished : PartlyPublished;
            }

            // O3: choosing again. A destination that landed, or may have, in an earlier publish or in this
            // pane, is sent again only once the technician has looked; one that plainly failed goes
            // without it (ST-094 AC3). Whether or not this pane has results of its own (m-d).
            if (ChosenMayDuplicate && !TicketChecked)
            {
                return CheckTheTicketFirst;
            }

            if (!HasIntegrations)
            {
                return ConnectPrompt;
            }

            if (NoteBanners.PublishBlockedBecause(_session, ticketChosen: Ticket is not null, _offline, _draftPending) is { } reason)
            {
                return reason;
            }

            return ChosenDestinations().Count == 0 ? "Choose at least one destination" : null;
        }
    }

    public bool CanPublish => BlockedBecause is null && !IsPublishing;

    /// <summary>The service's refusal that sent the pane back to the choice, said once the choice is open (m-g). Null otherwise.</summary>
    public string? EarlierRefusal { get; private set; }

    /// <summary>Spec §6's toast line for what happened, or null before anything has.</summary>
    public string? Summary
    {
        get
        {
            if (_results.Count == 0)
            {
                return null;
            }

            // A destination that landed without a screenshot is published, and says what is missing
            // rather than letting "Published" hide it (2026-10-12 review, S-69).
            var missing = string.Join(' ', _results.Values.Where(r => r is { Ok: true, Kind: DestinationResult.Partial, Error.Length: > 0 }).OrderBy(r => r.Destination).Select(r => r.Error));
            if (Published)
            {
                var time = _results.TryGetValue(Destination.TimeEntry, out var entry) && entry.Ok
                    ? $" · {Minutes / 60}:{Minutes % 60:00} logged"
                    : string.Empty;
                return missing.Length > 0
                    ? $"Published to ticket #{Ticket?.Id}{time}. {missing}"
                    : $"Published to ticket #{Ticket?.Id}{time}";
            }

            var failed = _results.Values.Where(r => !r.Ok).ToList();
            var landed = _results.Values.Where(r => r.Ok).Select(r => Name(r.Destination)).ToList();
            var first = failed[0];
            var prefix = landed.Count > 0 ? $"{Capitalise(landed[0])} published. " : string.Empty;
            // One that may have landed is not called failed: "failed" reads as "publish again". One the
            // service refused as already published is said as that, since part of it is on the ticket.
            var outcome = first.MayHaveLanded ? "not confirmed" : "failed";
            var line = first.Kind switch
            {
                // The prompt below says why and holds the choice; the summary only points at it.
                DestinationResult.NeedsMapping when first.Destination == Destination.KbArticle && NeedsMapping =>
                    $"{prefix}KB article not sent yet: choose its company below",
                DestinationResult.AlreadyPublished => $"{prefix}Already published: check the ticket before publishing again",
                DestinationResult.SessionUnconfirmed => $"{prefix}An earlier publish was not confirmed: check the ticket before publishing again",
                _ => $"{prefix}{Capitalise(Name(first.Destination))} {outcome}: {first.Error ?? "no reason given"}",
            };
            return missing.Length > 0 ? $"{line} {missing}" : line;
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

    /// <summary>
    /// The list on focus, before anything is typed (Spec §5 S3): a search for nothing, which the service
    /// and the backend read as "the recent ones". Returns whether it asked.
    /// </summary>
    public async Task<bool> RecentAsync(CancellationToken ct = default)
    {
        if (!HasIntegrations)
        {
            return false;
        }

        Matches = await _search(string.Empty, ct).ConfigureAwait(false);
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
    /// disabled for a reason, and a shortcut must not get past it. That includes after anything has
    /// landed, when <see cref="RetryAsync"/> is the only way on.
    /// </summary>
    public Task<IReadOnlyList<DestinationResult>> PublishAsync(DraftNote note, IReadOnlyList<string> frameIds, CancellationToken ct = default)
    {
        if (BlockedBecause is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        return SendAsync(ChosenDestinations(), note, frameIds, ct);
    }

    /// <summary>
    /// Sends only what failed. The note that landed is not sent again (ST-094 AC3). Refused while
    /// something may already be on the ticket and the technician has not said they checked.
    /// </summary>
    public Task<IReadOnlyList<DestinationResult>> RetryAsync(DraftNote note, IReadOnlyList<string> frameIds, CancellationToken ct = default)
    {
        if (RetryBlockedBecause is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

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

        // The retry below would send whatever failed, including one that may already be on the ticket.
        if (RetryBlockedBecause is { } reason)
        {
            throw new InvalidOperationException(reason);
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

        // Only the technician's "I checked the ticket" says a resend is meant. The service checks each
        // destination sent against what it holds, so a Retry of what plainly failed beside a landing goes
        // without the flag (ST-094 AC3), and a pane that saw something land does not vouch for a
        // destination another window has since left unknown (re-review of #265 at bde6a99; the owner's
        // earlier silent flag on Retry is withdrawn).
        var again = TicketChecked;
        TicketChecked = false;
        EarlierRefusal = null;
        IsPublishing = true;
        try
        {
            var results = await _publish(new PublishRequest(_session.SessionId, Ticket!, NoteType, Minutes, destinations, note, frameIds, again), ct).ConfigureAwait(false);
            foreach (var result in results)
            {
                _results[result.Destination] = result;
                Remember(result);
            }

            // The service knew of an earlier publish this pane did not: ask it what is on the ticket and
            // offer the choice, rather than a result list that only says no (O3).
            // One that comes back with nothing (a dropped pipe, a session gone) leaves the refusal rows,
            // which count as may-have-landed, so the box to tick shows and Publish never loops silently (m-a).
            if (_reloadEarlier is not null && results.Count > 0 && results.All(r => r.RefusedForAnEarlierPublish))
            {
                var earlier = await _reloadEarlier(ct).ConfigureAwait(false);
                if (earlier is { Count: > 0 })
                {
                    _results.Clear();
                    _earlier.Clear();
                    OfferEarlier(earlier);
                    EarlierRefusal = results[0].Error;
                }
            }

            return Results;
        }
        finally
        {
            IsPublishing = false;

            // Cleared again: a tick given while this send was on its way was about the answer before it,
            // and must not let the send after it through unseen (review of #265).
            TicketChecked = false;
        }
    }

    /// <summary>Starts the pane on what earlier publishes left: those destinations unticked, the rest as they are.</summary>
    private void OfferEarlier(IReadOnlyDictionary<Destination, EarlierOutcome>? earlier)
    {
        if (earlier is null || earlier.Count == 0)
        {
            return;
        }

        foreach (var (destination, outcome) in earlier)
        {
            _earlier[destination] = outcome;
        }

        TicketNote &= !_earlier.ContainsKey(Destination.TicketNote);
        TimeEntry &= !_earlier.ContainsKey(Destination.TimeEntry);
        KbArticle &= !_earlier.ContainsKey(Destination.KbArticle);
    }

    /// <summary>The same rule as the service's record: landed stays landed, may-have-landed is unknown, a plain failure after a check drops out.</summary>
    private void Remember(DestinationResult result)
    {
        // Neither refusal says anything new about the ticket: one for an earlier publish is about what the
        // service holds, and one for the whole request (m-c) is about the request.
        if (result.RefusedForAnEarlierPublish || result is { Ok: false, Kind: DestinationResult.Refused })
        {
            return;
        }

        if (result.Ok)
        {
            _earlier[result.Destination] = EarlierOutcome.Landed;
        }
        else if (EarlierFor(result.Destination) == EarlierOutcome.Landed)
        {
            return;
        }
        else if (result.MayHaveLanded)
        {
            _earlier[result.Destination] = EarlierOutcome.Unknown;
        }
        else
        {
            _ = _earlier.Remove(result.Destination);
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
