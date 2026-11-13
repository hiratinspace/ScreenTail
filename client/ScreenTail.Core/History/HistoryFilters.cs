using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.History;

/// <summary>
/// The All sessions table's period filter (spec v0.6.9 §5 S4). Whole days in the technician's zone, today
/// included, rather than a pair of date pickers: the questions asked of a history are "today", "this
/// week" and "this month", and three choices answer them in one click each.
/// </summary>
public enum HistoryPeriod
{
    AnyTime,
    Today,
    Last7Days,
    Last30Days,
}

/// <summary>What the All sessions table shows: a period, a wire status and a tool kind, each null or <see cref="HistoryPeriod.AnyTime"/> for all.</summary>
public sealed record TableFilter(HistoryPeriod Period, string? Status, string? Tool)
{
    /// <summary>Every row: what the table opens with.</summary>
    public static readonly TableFilter Any = new(HistoryPeriod.AnyTime, null, null);

    /// <summary>Whether any filter leaves rows out, so the table can offer Clear filters.</summary>
    public bool IsActive => Period != HistoryPeriod.AnyTime || Status is not null || Tool is not null;
}

/// <summary>One entry of a filter's list: the words it shows and the value it filters by (null for all).</summary>
public sealed record FilterChoice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The All sessions table's filters (spec v0.5 §5 S4: date range, status, tool; built in v0.6.9). The
/// statuses are named in the badges' words (<see cref="StatusBadges"/>), the tools by their registry names
/// and only those the rows have, and the period in whole days. The rail's search and groups are its own
/// filters, so these live on the table.
/// </summary>
public static class HistoryFilters
{
    /// <summary>The line in place of the table when the filters leave nothing.</summary>
    public const string NoMatch = "No sessions match these filters.";

    public static IReadOnlyList<FilterChoice<string?>> StatusChoices { get; } =
    [
        new("Any status", null),
        new(StatusBadges.Draft.Word, "draft"),
        new(StatusBadges.Pending.Word, "pending"),
        new(StatusBadges.Partial.Word, "partial"),
        new(StatusBadges.Published.Word, "published"),
        new(StatusBadges.Discarded.Word, "discarded"),
    ];

    public static IReadOnlyList<FilterChoice<HistoryPeriod>> PeriodChoices { get; } =
    [
        new("Any time", HistoryPeriod.AnyTime),
        new("Today", HistoryPeriod.Today),
        new("Last 7 days", HistoryPeriod.Last7Days),
        new("Last 30 days", HistoryPeriod.Last30Days),
    ];

    /// <summary>"Any tool", then each tool the rows have, by its registry name in alphabetical order.</summary>
    public static IReadOnlyList<FilterChoice<string?>> ToolChoices(IEnumerable<SessionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            new("Any tool", null),
            .. rows.Select(r => r.Tool)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(tool => new FilterChoice<string?>(ToolNames.Display(tool), tool))
                .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>The rows the filter keeps, in the order given.</summary>
    public static IReadOnlyList<SessionRow> Apply(IEnumerable<SessionRow> rows, TableFilter filter, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTime? from = filter.Period switch
        {
            HistoryPeriod.Today => today,
            HistoryPeriod.Last7Days => today.AddDays(-6),
            HistoryPeriod.Last30Days => today.AddDays(-29),
            _ => null,
        };

        return
        [
            .. rows.Where(row =>
                (from is not { } start || TimeZoneInfo.ConvertTime(row.StartedAt, zone).Date >= start)
                && (filter.Status is null || string.Equals(row.Status, filter.Status, StringComparison.OrdinalIgnoreCase))
                && (filter.Tool is null || string.Equals(row.Tool, filter.Tool, StringComparison.OrdinalIgnoreCase))),
        ];
    }
}

/// <summary>
/// The rail's width (spec v0.6.9 §5 S4): <c>layout.rail-width</c> in a window at least
/// <c>layout.rail-narrow-below</c> wide, and <c>layout.rail-width-narrow</c> below it, so the session pane
/// keeps room for Review and the All sessions table at the 1024 minimum. Narrowed rather than collapsed:
/// the rail is where the sessions and the capture state are, and hiding it would hide both.
/// </summary>
public static class RailLayout
{
    public static bool IsWide(double windowWidth, double breakpoint) => windowWidth >= breakpoint;
}
