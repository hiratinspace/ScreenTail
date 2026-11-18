using System.Globalization;
using ScreenTrail.Core.Shell;
using ScreenTrail.Core.Store;
using ScreenTrail.Shared.Ipc;

namespace ScreenTrail.Core.History;

/// <summary>The banner's words: the line, the reasons under it (empty for none), and what its tool tip and screen reader add.</summary>
/// <param name="Headline">"Captured 46 of 52 remote sessions in the last 7 days (88%)."</param>
/// <param name="Detail">"6 not captured: auto-start off 4, tool switched off 2." Empty when nothing was missed.</param>
/// <param name="Help">How the numbers are counted, for the tool tip and the screen reader's help text.</param>
public sealed record CoverageWords(string Headline, string Detail, string Help)
{
    /// <summary>What a screen reader says for the banner: the line and the reasons.</summary>
    public string Spoken => Detail.Length == 0 ? Headline : $"{Headline} {Detail}";
}

/// <summary>
/// The coverage banner on All sessions (ST-079, spec v0.7.3 S4): above the table, under the filters,
/// describing the rows they leave. "Captured 46 of 52 remote sessions in the last 7 days (88%)", with the
/// misses explained in a line under it. Every choice below was made by the owner's rule (pick the
/// friendliest, record it) and is recorded in S4.
/// </summary>
public static class CoverageBanner
{
    /// <summary>
    /// The banner for the filters as they stand, or null when it has nothing honest to say:
    /// <list type="bullet">
    /// <item>no counts (the service did not answer, or answered for another period than the one shown);</item>
    /// <item>a status filter is on: coverage is about whether a remote session was captured at all, and a
    /// missed one has no status, so "3 of 5 drafts" would be a number with no meaning;</item>
    /// <item>nothing to count: no session captured and none missed in the period.</item>
    /// </list>
    /// </summary>
    /// <param name="reported">The service's counts for <paramref name="askedFrom"/>.</param>
    /// <param name="askedFrom">The period start the counts were asked for (null for any time).</param>
    public static CoverageWords? Of(CoverageReported? reported, DateTimeOffset? askedFrom, TableFilter filter, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(zone);
        if (reported is null || filter.Status is not null || askedFrom != HistoryFilters.From(filter.Period, now, zone))
        {
            return null;
        }

        var coverage = SessionHistory.CoverageOf(reported, filter.Tool);
        if (coverage.Total == 0)
        {
            return null;
        }

        var what = filter.Tool is null ? "remote" : ToolNames.Display(filter.Tool);
        var when = Period(filter.Period, askedFrom, reported.CountedFrom, now, zone);
        var headline = coverage.NotCaptured == 0
            ? coverage.Captured == 1
                ? $"Captured the only {what} session {when}."
                : string.Create(CultureInfo.CurrentCulture, $"Captured all {coverage.Captured} {what} sessions {when}.")
            : string.Create(CultureInfo.CurrentCulture, $"Captured {coverage.Captured} of {coverage.Total} {what} {(coverage.Total == 1 ? "session" : "sessions")} {when} ({coverage.Percent}%).");
        return new CoverageWords(headline, Detail(coverage), Help(reported.RetentionDays));
    }

    /// <summary>The reasons, most first: "6 not captured: auto-start off 4, tool switched off 2."</summary>
    public static string Detail(Coverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.NotCaptured == 0)
        {
            return string.Empty;
        }

        var reasons = string.Join(", ", coverage.Missed.Select(m => string.Create(CultureInfo.CurrentCulture, $"{Words(m.Reason)} {m.Count}")));
        return string.Create(CultureInfo.CurrentCulture, $"{coverage.NotCaptured} not captured: {reasons}.");
    }

    /// <summary>Each reason in a few words, as the technician would say it.</summary>
    public static string Words(MissReason reason) => reason switch
    {
        MissReason.AutoStartOff => "auto-start off",
        MissReason.ToolSwitchedOff => "tool switched off",
        MissReason.StartHeld => "update installing",
        MissReason.NoIndicator => "app not running",
        _ => "last session still finishing",
    };

    /// <summary>
    /// The period in words. The filter's own words while the counts cover all of it; "since" the day the
    /// counts start when the device cannot count that far back (it keeps missed sessions as long as raw
    /// data, and began counting when this version was installed), so the banner never claims a period it
    /// did not count. Any time always says since when.
    /// </summary>
    private static string Period(HistoryPeriod period, DateTimeOffset? askedFrom, DateTimeOffset countedFrom, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (askedFrom is { } from && countedFrom <= from)
        {
            return period switch
            {
                HistoryPeriod.Today => "today",
                HistoryPeriod.Last7Days => "in the last 7 days",
                _ => "in the last 30 days",
            };
        }

        var start = TimeZoneInfo.ConvertTime(countedFrom, zone);
        return start.Date == TimeZoneInfo.ConvertTime(now, zone).Date
            ? string.Create(CultureInfo.CurrentCulture, $"since {start:HH:mm} today")
            : string.Create(CultureInfo.CurrentCulture, $"since {start:ddd d MMM}");
    }

    private static string Help(int retentionDays) => string.Create(
        CultureInfo.CurrentCulture,
        $"A remote session counts once, however often you switch away and back within the grace period. Brief pass-throughs, under a minute in the tool, are not counted. ScreenTrail keeps its count of missed sessions for {retentionDays} {(retentionDays == 1 ? "day" : "days")}, as long as it keeps screenshots.");
}

/// <summary>
/// The service's answer to <c>get_coverage</c> (contract v14): the counts from the period's start, or from
/// the earliest moment the device can count from honestly, whichever is later. Platform-neutral, over the
/// store's interface, so the Mac suite runs it.
/// </summary>
/// <param name="retention">The retention in force (INV-12): missed sessions are kept that long and no longer.</param>
public sealed class CoverageCommands(ICoverageStore store, Func<TimeSpan> retention, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CoverageReported> ReplyAsync(GetCoverageCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var kept = retention();
        var earliest = Later(await store.CoverageCountedSinceAsync(ct).ConfigureAwait(false), _time.GetUtcNow() - kept);
        var from = command.From is { } asked ? Later(asked, earliest) : earliest;
        var counts = await store.CountCoverageAsync(from, ct).ConfigureAwait(false);

        var tools = counts.Captured.Keys
            .Concat(counts.Missed.Keys.Select(k => k.Tool))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(tool => new ToolCoverage
            {
                Tool = tool,
                Captured = counts.Captured.GetValueOrDefault(tool),
                Missed = [.. counts.Missed
                    .Where(m => string.Equals(m.Key.Tool, tool, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.Key.Reason)
                    .Select(m => new MissedCount { Reason = MissReasons.Wire(m.Key.Reason), Count = m.Value })],
            })
            .ToList();

        return new CoverageReported
        {
            RequestId = command.RequestId,
            CountedFrom = from,
            RetentionDays = (int)Math.Round(kept.TotalDays),
            Tools = tools,
        };
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
