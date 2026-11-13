using System.Globalization;

namespace ScreenTail.Core.History;

/// <summary>
/// A session's state as a badge: its word, the Segoe Fluent glyph beside the word, and which state hue
/// the glyph and the edge take. The word and the glyph carry the state, so it reads in high contrast and
/// to anyone who does not see the hue; the hue only agrees with them (Spec §3 Badge, §7).
/// </summary>
/// <param name="Tone">
/// <c>Accent</c> (a draft), <c>Published</c>, <c>Paused</c> (partial capture, not confirmed),
/// <c>Offline</c> (no draft yet) or <c>Muted</c> (discarded, or a state this build does not know). The view
/// maps each to its <c>state.*</c> token on the content surfaces and its <c>shell.*</c> token on the frame.
/// </param>
public sealed record StatusBadge(string Word, string Glyph, string Tone);

/// <summary>
/// One rule for the status badges on the inbox rail and in the All sessions table (spec v0.6.9 §5 S4), so
/// a state has the same word, glyph and hue wherever it is shown.
/// </summary>
public static class StatusBadges
{
    /// <summary>Edit (pencil): a note waiting to be checked.</summary>
    public static readonly StatusBadge Draft = new("Draft", "", "Accent");

    /// <summary>Check mark.</summary>
    public static readonly StatusBadge Published = new("Published", "", "Published");

    /// <summary>Warning triangle: the note may be missing steps.</summary>
    public static readonly StatusBadge Partial = new("Partial capture", "", "Paused");

    /// <summary>Stopwatch: still being captured, being drafted, or a failed draft.</summary>
    public static readonly StatusBadge Pending = new("No draft yet", "", "Offline");

    /// <summary>Delete (bin).</summary>
    public static readonly StatusBadge Discarded = new("Discarded", "", "Muted");

    /// <summary>Unknown (a question mark): a publish that may have reached the ticket (contract v10).</summary>
    public static readonly StatusBadge NotConfirmed = new(SessionHistory.NotConfirmedLabel, "", "Paused");

    /// <summary>The badge for a wire status (<c>draft</c>, <c>published</c>, <c>partial</c>, <c>pending</c>, <c>discarded</c>).</summary>
    public static StatusBadge Of(string? status)
    {
        if (Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var known)
            && Enum.IsDefined(known)
            && !int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return Of(known);
        }

        // A state this build does not know: its own word, with the info glyph, and no hue that claims anything.
        var word = string.IsNullOrWhiteSpace(status) ? "Unknown" : char.ToUpperInvariant(status[0]) + status[1..];
        return new StatusBadge(word, "", "Muted");
    }

    public static StatusBadge Of(SessionStatus status) => status switch
    {
        SessionStatus.Draft => Draft,
        SessionStatus.Published => Published,
        SessionStatus.Partial => Partial,
        SessionStatus.Pending => Pending,
        _ => Discarded,
    };
}
