using System.Globalization;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.History;

/// <summary>The inbox rail's groups, in the order the rail shows them (Spec v0.6.0 §5 S4).</summary>
public enum InboxGroup
{
    /// <summary>A draft waiting, a capture that came out partial, or a session with no draft yet.</summary>
    ToCheck,

    /// <summary>A publish that may have reached the ticket and was never confirmed (contract v10).</summary>
    NotConfirmed,

    /// <summary>Published, from a session started today.</summary>
    PublishedToday,

    /// <summary>Everything else: published before today, and discarded. Collapsed until opened.</summary>
    Earlier,
}

/// <summary>One row of the rail, already worded.</summary>
/// <param name="Title">The row's heading: the company, else the note's title, else "ScreenConnect session" (the tool by its registry name, Spec S1).</param>
/// <param name="Age">"14 min ago", "2 h ago", "Yesterday", or the day.</param>
/// <param name="Meta">"#48213 · ScreenConnect · 12 min" for a row that names its session, else "12 min · 7 screenshots".</param>
/// <param name="Badge">The state, where the group does not already say it; null when it does.</param>
/// <param name="Spoken">The row as a screen reader says it, the start included so no two rows read the same.</param>
/// <param name="Subtitle">The note's title under the company, when both are known; null otherwise.</param>
public sealed record InboxItem(SessionRow Row, InboxGroup Group, string Title, string Age, string Meta, string? Badge, string Spoken, string? Subtitle = null);

/// <summary>
/// The inbox rail (Spec v0.6.0 §5 S4): History as the workspace's left rail, grouped by what each session
/// needs. Platform-neutral, so the grouping, the words and the search are tested on any OS (ADR-0002).
///
/// The rows are History's (<see cref="SessionRow"/>): a tool, a start, a duration, a status, a count of
/// screenshots and the not-confirmed mark, and since contract v11 the note's title, the ticket and the
/// published ticket's company when the service knows them. A row says what it has and leaves out what is
/// not known, never a placeholder; a row with none of the three reads as it did in v0.6.4. The search
/// looks through the same words (Spec v0.6.9, "As built" under S4).
/// </summary>
public static class InboxRail
{
    /// <summary>The search box's hint, the target's words (Spec §5 S4).</summary>
    public const string SearchHint = "Company, ticket or title";

    /// <summary>The search box's spoken help, with its key.</summary>
    public const string SearchHelp = "Ctrl+F. Shows the sessions whose company, ticket, title, tool, day or state has these words. Esc clears it.";

    /// <summary>The rail before the service has answered.</summary>
    public const string Asking = "Asking the capture service…";

    /// <summary>The rail when the service did not answer.</summary>
    public const string NotAnswered = "The capture service did not answer. The sessions show here when it does.";

    /// <summary>What a group is called on the rail and to a screen reader.</summary>
    public static string Name(InboxGroup group) => group switch
    {
        InboxGroup.ToCheck => "To check",
        InboxGroup.NotConfirmed => SessionHistory.NotConfirmedLabel,
        InboxGroup.PublishedToday => "Published today",
        _ => "Earlier",
    };

    /// <summary>The line in place of the rows when the search matches nothing.</summary>
    public static string NoMatch(string query) => $"No sessions match “{query.Trim()}”.";

    /// <summary>
    /// Which group a row belongs in. A discarded session is Earlier whenever it was, since nothing can be
    /// done with it; a not-confirmed one has its own group whatever its status, so settling one is a few
    /// actions; a status this build does not know waits in To check, where someone will look at it.
    /// </summary>
    public static InboxGroup GroupOf(SessionRow row, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(zone);
        var status = Status(row);
        if (status == SessionStatus.Discarded)
        {
            return InboxGroup.Earlier;
        }

        if (row.PublishUnconfirmed)
        {
            return InboxGroup.NotConfirmed;
        }

        if (status == SessionStatus.Published)
        {
            // The row has no time of publishing, so a session counts as published today when it started
            // today. A session started before midnight and published after it is Earlier.
            return Day(row.StartedAt, zone) == Day(now, zone) ? InboxGroup.PublishedToday : InboxGroup.Earlier;
        }

        return InboxGroup.ToCheck;
    }

    /// <summary>
    /// The state badge, only where the group does not already say it (Spec §5 S4): Published in Earlier,
    /// Discarded, a partial capture, no draft yet. Null in To check for a plain draft, in Not confirmed and
    /// in Published today.
    /// </summary>
    public static string? Badge(SessionRow row, InboxGroup group)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Status(row) switch
        {
            SessionStatus.Discarded or SessionStatus.Partial or SessionStatus.Pending => StatusBadges.Of(row.Status).Word,
            SessionStatus.Published when group == InboxGroup.Earlier => StatusBadges.Published.Word,
            SessionStatus.Draft or SessionStatus.Published => null,
            _ => row.Status.Length == 0 ? null : StatusBadges.Of(row.Status).Word,
        };
    }

    /// <summary>How long ago the session started: "Just now", "14 min ago", "2 h ago", "Yesterday", then the day.</summary>
    public static string Age(DateTimeOffset started, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var ago = now - started;
        var today = Day(now, zone);
        var day = Day(started, zone);
        if (ago < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalMinutes} min ago");
        }

        if (day == today)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalHours} h ago");
        }

        if (day == today.AddDays(-1))
        {
            return "Yesterday";
        }

        var local = TimeZoneInfo.ConvertTime(started, zone);
        return local.Year == TimeZoneInfo.ConvertTime(now, zone).Year
            ? local.ToString("ddd d MMM", CultureInfo.InvariantCulture)
            : local.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>"Under 1 min", "12 min", "1 h 5 min".</summary>
    public static string Duration(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (span < TimeSpan.FromMinutes(1))
        {
            return "Under 1 min";
        }

        var minutes = (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min");
    }

    /// <summary>The row's second line: how long, and how many screenshots ("1 screenshot", "7 screenshots").</summary>
    public static string Meta(SessionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{Duration(row.DurationMs)} · {Screenshots(row.Frames)}";
    }

    /// <summary>
    /// Whether the row has every word of the search, case ignored, in what it says or could be called:
    /// its tool, its group and state ("to check", "draft", "partial", "not confirmed", "published",
    /// "discarded"), its day ("today", "yesterday", "Tue", "10 Nov") and its start ("14:02"). An empty
    /// search matches every row.
    /// </summary>
    public static bool Matches(SessionRow row, string? query, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(zone);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var group = GroupOf(row, now, zone);
        var local = TimeZoneInfo.ConvertTime(row.StartedAt, zone);
        var day = Day(row.StartedAt, zone);
        var today = Day(now, zone);
        var words = string.Join(
            ' ',
            SessionHistory.Known(row.Company) ?? string.Empty,
            SessionHistory.Known(row.Title) ?? string.Empty,
            SessionHistory.Known(row.Ticket) is { } ticket ? $"#{ticket}" : string.Empty,
            ToolNames.Display(row.Tool),
            row.Tool ?? string.Empty,
            Name(group),
            row.Status,
            Badge(row, group) ?? string.Empty,
            row.PublishUnconfirmed ? SessionHistory.NotConfirmedLabel : string.Empty,
            day == today ? "today" : day == today.AddDays(-1) ? "yesterday" : string.Empty,
            local.ToString("dddd ddd d MMMM MMM yyyy HH:mm", CultureInfo.InvariantCulture));

        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => words.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The rows the search leaves, worded, in the rail's order: by group, then newest first.</summary>
    public static IReadOnlyList<InboxItem> Items(IEnumerable<SessionRow> rows, string? query, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(zone);
        return
        [
            .. rows
                .Where(row => Matches(row, query, now, zone))
                .Select(row => Item(row, now, zone))
                .OrderBy(item => item.Group)
                .ThenByDescending(item => item.Row.StartedAt),
        ];
    }

    /// <summary>One row, worded.</summary>
    public static InboxItem Item(SessionRow row, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(zone);
        var group = GroupOf(row, now, zone);
        var company = SessionHistory.Known(row.Company);
        var note = SessionHistory.Known(row.Title);
        var ticket = SessionHistory.Known(row.Ticket);
        var tool = ToolNames.Display(row.Tool);
        var shots = Screenshots(row.Frames);
        var duration = Duration(row.DurationMs);

        // The target's order (Spec §5 S4): the company, then the note's title, then ticket, tool and
        // duration. What is not known is left out. Headed by the tool, the row keeps its screenshots, since
        // the tool is already said; headed by a company or a title, the tool takes their place on the line.
        var named = company is not null || note is not null;
        var title = company ?? note ?? $"{tool} session";
        var subtitle = company is not null ? note : null;
        var hash = ticket is null ? null : $"#{ticket}";
        var meta = named
            ? Join(" · ", hash, tool, duration)
            : Join(" · ", hash, duration, shots);
        var age = Age(row.StartedAt, now, zone);
        var badge = Badge(row, group);
        var started = TimeZoneInfo.ConvertTime(row.StartedAt, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        var heard = named || ticket is not null
            ? Join(", ", ticket is null ? null : $"ticket {ticket}", named ? tool : null, $"{duration} · {shots}")
            : meta;
        var spoken = Join(", ", title, subtitle, badge, age, $"started {started}", heard);
        return new InboxItem(row, group, title, age, meta, badge, spoken, subtitle);
    }

    private static string Screenshots(int frames) =>
        frames == 1 ? "1 screenshot" : string.Create(CultureInfo.InvariantCulture, $"{frames} screenshots");

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>A group's heading as a screen reader hears it: "To check, 2 sessions".</summary>
    public static string Heading(InboxGroup group, int count) =>
        count == 1 ? $"{Name(group)}, 1 session" : string.Create(CultureInfo.InvariantCulture, $"{Name(group)}, {count} sessions");

    private static SessionStatus? Status(SessionRow row) =>
        Enum.TryParse<SessionStatus>(row.Status, ignoreCase: true, out var status)
        && Enum.IsDefined(status)
        && !int.TryParse(row.Status, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? status
            : null;

    private static DateTime Day(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).Date;
}
