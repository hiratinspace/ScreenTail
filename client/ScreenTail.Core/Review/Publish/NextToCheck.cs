using System.Globalization;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Review.Publish;

/// <summary>The receipt's "Today" card: what was published today and what is left to check.</summary>
public sealed record TodaySummary(int Published, int LeftToCheck);

/// <summary>
/// The receipt's "Next to check" (Spec v0.6.0 §1.1, §5 S3): every ending puts the next session one key
/// away. The inbox's order is To check (drafts waiting) and then Not confirmed (a publish that may have
/// landed), newest first in each, and the session just published is never next.
///
/// It reads History's rows, which carry a tool, a duration and a start, never a title or a company
/// (INV-10 keeps the list content-free), so the card says what a row can and nothing more.
/// </summary>
public static class NextToCheck
{
    private const string Draft = "draft";

    /// <summary>The next session to check, or null when nothing else waits.</summary>
    public static SessionRow? Pick(IReadOnlyList<SessionRow>? rows, string currentId)
    {
        if (rows is null)
        {
            return null;
        }

        var others = rows.Where(r => !string.Equals(r.Id, currentId, StringComparison.Ordinal)).ToList();
        return others
                .Where(r => r.Status == Draft && !r.PublishUnconfirmed)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault()
            ?? others
                .Where(r => r.PublishUnconfirmed && r.Status != "discarded")
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault();
    }

    /// <summary>The card's first line: "Remote Desktop session", by the tool's registry name (Spec S1).</summary>
    public static string Title(SessionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"{ToolNames.Display(row.Tool)} session";
    }

    /// <summary>The card's second line: "9 min · started 13:00", and "publish not confirmed" when that is why it waits.</summary>
    public static string Meta(SessionRow row, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(zone);
        var minutes = Math.Max(1, (int)Math.Round(TimeSpan.FromMilliseconds(row.DurationMs).TotalMinutes, MidpointRounding.AwayFromZero));
        var started = TimeZoneInfo.ConvertTime(row.StartedAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var meta = string.Create(CultureInfo.InvariantCulture, $"{minutes} min · started {started}");
        return row.PublishUnconfirmed ? $"{meta} · publish not confirmed" : meta;
    }

    /// <summary>
    /// The day so far: sessions published today (the one just published among them, whatever the list
    /// said when it was read), and sessions still waiting to be checked.
    /// </summary>
    public static TodaySummary Today(IReadOnlyList<SessionRow>? rows, string currentId, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (rows is null)
        {
            return new TodaySummary(1, 0);
        }

        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var published = rows.Count(r =>
            !string.Equals(r.Id, currentId, StringComparison.Ordinal)
            && r.Status is "published" or "partial"
            && TimeZoneInfo.ConvertTime(r.StartedAt, zone).Date == today);
        var left = rows.Count(r =>
            !string.Equals(r.Id, currentId, StringComparison.Ordinal)
            && ((r.Status == Draft && !r.PublishUnconfirmed) || (r.PublishUnconfirmed && r.Status != "discarded")));
        return new TodaySummary(published + 1, left);
    }
}
