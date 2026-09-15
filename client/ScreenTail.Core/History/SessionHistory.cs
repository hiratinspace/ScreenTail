using System.Globalization;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;

namespace ScreenTail.Core.History;

/// <summary>The statuses Spec §5 S4 filters by. Derived from the session, never stored separately.</summary>
public enum SessionStatus
{
    /// <summary>Still running, or finalising.</summary>
    Pending,

    /// <summary>Drafted and waiting for someone to review it.</summary>
    Draft,

    Published,

    /// <summary>The technician threw it away. The row stays so the history can say so (INV-12).</summary>
    Discarded,

    /// <summary>Capture started late or was interrupted, so the note may be missing steps.</summary>
    Partial,
}

public sealed record HistoryFilter
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary>Empty means every status, which is what the screen opens with.</summary>
    public IReadOnlySet<SessionStatus> Statuses { get; init; } = new HashSet<SessionStatus>();

    /// <summary>Remote-tool kinds as they appear in the schema; empty means all.</summary>
    public IReadOnlySet<string> Tools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Captured">Sessions ScreenTail recorded.</param>
/// <param name="Total">Remote sessions that happened, captured or not.</param>
public sealed record Coverage(int Captured, int Total)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(Captured * 100.0 / Total, MidpointRounding.AwayFromZero);

    /// <summary>Spec §5 S4's banner. Null when there is nothing to report rather than "0 of 0 (0%)".</summary>
    public string? Banner => Total == 0
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"Captured {Captured} of {Total} remote sessions ({Percent}%)");
}

/// <summary>
/// The session history screen's contents (ST-079, Spec §5 S4).
///
/// Status is derived here rather than stored, for the same reason the note pane's banners are: it is a
/// claim about a session, and the only thing entitled to make it is the session itself. A status column
/// written at finalize would go on saying "draft" after the note was published, and would say nothing
/// about a session that was discarded afterwards.
///
/// Platform-neutral, so the filtering and the coverage arithmetic can be argued about without a window —
/// which matters because Spec §5 S4 renders two hundred rows and the ticket puts a number on it.
/// </summary>
public static class SessionHistory
{
    /// <summary>
    /// What the row's badge says.
    ///
    /// Order matters: discarded wins over everything, because a session someone threw away is not a draft
    /// waiting for them however complete it was. Partial comes before draft for the opposite reason — a
    /// note with missing steps should not sit in the list looking like any other.
    /// </summary>
    public static SessionStatus StatusOf(SessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.Equals(session.State, SessionStateNames.Discarded, StringComparison.Ordinal))
        {
            return SessionStatus.Discarded;
        }

        if (string.Equals(session.State, SessionStateNames.Published, StringComparison.Ordinal))
        {
            return SessionStatus.Published;
        }

        if (session.PartialCapture)
        {
            return SessionStatus.Partial;
        }

        return session.Title is not null ? SessionStatus.Draft : SessionStatus.Pending;
    }

    /// <summary>Newest first, filtered. The store already returns them in order; this preserves it.</summary>
    public static IReadOnlyList<SessionSummary> Filter(IEnumerable<SessionSummary> sessions, HistoryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        filter ??= new HistoryFilter();

        return
        [
            .. sessions.Where(session =>
                (filter.From is not { } from || session.StartedAt >= from)

                // Inclusive of the end day's sessions: a technician picking "to Friday" means Friday, and
                // a range that silently excluded Friday's work would look like the history losing rows.
                && (filter.To is not { } to || session.StartedAt <= to)
                && (filter.Statuses.Count == 0 || filter.Statuses.Contains(StatusOf(session)))
                && (filter.Tools.Count == 0 || filter.Tools.Contains(session.RemoteTool))),
        ];
    }

    /// <summary>
    /// The coverage banner, over whatever the filters left.
    ///
    /// Spec §5 S4 puts it above a filtered table, so it has to describe that table: a banner still
    /// reporting the week while the technician is looking at one day is a number that answers a question
    /// nobody asked, and it is the kind of number people quote in a QBR.
    /// </summary>
    /// <param name="observed">Remote sessions detected, captured or not (ST-023). Null when unknown, and
    /// the banner then reports what was captured against itself rather than inventing a denominator.</param>
    public static Coverage CoverageOf(IReadOnlyList<SessionSummary> shown, int? observed = null)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var captured = shown.Count;
        return new Coverage(captured, Math.Max(observed ?? captured, captured));
    }

    /// <summary>
    /// What a bulk discard is about to destroy, for the typed confirmation (Spec §5 S4, v0.4.1 Q6).
    ///
    /// Already-discarded sessions are counted out: there is nothing left of them to destroy, and a
    /// confirmation asking a technician to type "5" when only three sessions will change is a
    /// confirmation that has misled them about the thing it exists to slow down.
    /// </summary>
    public static IReadOnlyList<SessionSummary> Discardable(IEnumerable<SessionSummary> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return [.. selected.Where(session => StatusOf(session) != SessionStatus.Discarded)];
    }
}
