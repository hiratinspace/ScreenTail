using System.Globalization;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>What a mark on the scrubber means. Spec §5 S3 gives each its own shape.</summary>
public enum TimelineMarkerKind
{
    /// <summary>A dot. Something the technician did.</summary>
    Click,

    /// <summary>A square. A screenshot, and the only kind that can be clicked through to.</summary>
    Frame,

    /// <summary>A flag. "Mark this moment" — the only frame anyone asked for by name.</summary>
    Marked,

    /// <summary>A striped band. Capture was off, and for how long.</summary>
    Suppressed,

    /// <summary>A violet band. In a session, deliberately not capturing this window (INV-5).</summary>
    OutOfScope,

    /// <summary>A tick under the strip. Something was said with no screenshot near it (ST-028).</summary>
    Narration,
}

/// <param name="EndMs">Where it stops. Equal to <paramref name="TsMs"/> for a point rather than a band.</param>
/// <param name="Position">Where to draw it, as a fraction of the session, so the view needs no arithmetic.</param>
/// <param name="FrameId">What to select when it is clicked. Only a frame marker has one.</param>
public sealed record TimelineMarker(
    TimelineMarkerKind Kind,
    long TsMs,
    long EndMs,
    double Position,
    string? FrameId = null,
    string? Description = null)
{
    public bool IsBand => EndMs > TsMs;
}

/// <param name="At">Wall-clock time, <c>14:02:10</c>. What a customer's event log is stamped with.</param>
/// <param name="FrameId">The screenshot this was said about, or null when there was none nearby.</param>
public sealed record TranscriptLine(string Id, string At, long TsMs, string Text, string? FrameId)
{
    /// <summary>
    /// The scrubber's monospace line, as Spec §5 S3 writes it.
    /// </summary>
    public string Display => $"[{At}] {Text}";

    /// <summary>
    /// Whether the redaction engine replaced something here.
    ///
    /// Not to hide it — it is already gone — but so the panel can show that something was removed rather
    /// than leaving a sentence that reads oddly for no visible reason.
    /// </summary>
    public bool HasRedaction => Text.Contains("[REDACTED]", StringComparison.Ordinal);
}

/// <summary>
/// Review's bottom panel: what happened, when, and what was said about it (ST-076, Spec §5 S3).
///
/// <b>This is where a draft is checked.</b> The note claims things; a technician verifies them by
/// clicking a sentence and seeing the screenshot it came from. Without that route the only way to check a
/// draft is to read the whole session, which is the work the product exists to remove — so the thirty
/// second review the whole design is built around rests on this panel being right.
///
/// Two rules shape it. <b>Nothing is invented</b>: a sentence with no screenshot near it jumps nowhere
/// rather than to the closest one, because showing a picture of something else and calling it evidence is
/// worse than showing nothing. And <b>a gap has a length</b>: ten seconds of a password field and four
/// minutes of one are different facts, so suppression is a band rather than a point.
///
/// In Core (ADR-0002) because it is arithmetic over a session, and because it decides what a technician
/// is shown as proof.
/// </summary>
public sealed class SessionTimeline
{
    /// <summary>Spec §5 S3: 40 px of scrubber when collapsed.</summary>
    public const double CollapsedHeight = 40;

    /// <summary>220 px expanded, which is about eight transcript lines.</summary>
    public const double ExpandedHeight = 220;

    /// <summary>
    /// How many click dots the strip will draw.
    ///
    /// A busy minute is two hundred clicks, and two hundred dots in a 40-pixel strip is a grey line that
    /// tells a technician less than ten dots would. Frames are never thinned this way: each one is a
    /// route to a screenshot, and losing one loses the only way to reach it from here.
    /// </summary>
    public const int MaxClickMarkers = 80;

    public SessionTimeline(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // A scrubber with no scale is a blank panel, so the last thing that happened is the end when
        // nothing else says otherwise.
        var last = new[]
        {
            session.DurationMs ?? 0,
            session.Frames.Count > 0 ? session.Frames.Max(f => f.TsMs) : 0,
            session.Transcript.Count > 0 ? session.Transcript.Max(t => t.EndMs) : 0,
            session.Events.Count > 0 ? session.Events.Max(e => e.TsMs) : 0,
        }.Max();

        DurationMs = Math.Max(1, last);

        Lines =
        [
            .. session.Transcript
                .OrderBy(segment => segment.TsMs)
                .ThenBy(segment => segment.Id, StringComparer.Ordinal)
                .Select(segment => new TranscriptLine(
                    segment.Id,
                    session.StartedAt.AddMilliseconds(segment.TsMs).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    segment.TsMs,
                    segment.Text,

                    // The aligner already decided this (ST-028). Guessing again here would mean two
                    // answers to one question, and the note's frame references come from the other one.
                    segment.FrameId)),
        ];

        Markers = [.. Build(session).OrderBy(marker => marker.TsMs).ThenBy(marker => marker.Kind)];
    }

    /// <summary>The scale everything is drawn against. Never zero.</summary>
    public long DurationMs { get; }

    public IReadOnlyList<TimelineMarker> Markers { get; }

    public IReadOnlyList<TranscriptLine> Lines { get; }

    /// <summary>The frame a transcript line points at, or null when it points at nothing.</summary>
    public string? FrameFor(string lineId) =>
        Lines.FirstOrDefault(line => line.Id == lineId)?.FrameId;

    private IEnumerable<TimelineMarker> Build(Session session)
    {
        foreach (var frame in session.Frames.Where(frame => !frame.RedactionPending))
        {
            // INV-1 reaches the timeline too: a marker that jumps to a frame nobody has read is a route
            // to an unredacted screenshot, however small the square is.
            yield return Point(TimelineMarkerKind.Frame, frame.TsMs, frame.Id);
        }

        foreach (var marker in session.Events.OfType<MarkerEvent>())
        {
            yield return Point(TimelineMarkerKind.Marked, marker.TsMs, marker.FrameId, "Marked by the technician");
        }

        // Narration is read off the transcript rather than off the NarrationEvent the aligner also writes.
        // Both say the same thing, and the transcript is the one this panel already has in hand: taking it
        // from the events would mean the tick and the line beneath it could disagree after an edit.
        foreach (var line in Lines.Where(line => line.FrameId is null))
        {
            yield return Point(TimelineMarkerKind.Narration, line.TsMs, null, "Said with no screenshot near it");
        }

        foreach (var band in Bands(session))
        {
            yield return band;
        }

        foreach (var click in Thin(session.Events.OfType<ClickEvent>().Select(click => click.TsMs).ToList()))
        {
            yield return Point(TimelineMarkerKind.Click, click, null);
        }
    }

    /// <summary>
    /// The stretches where capture was off, as bands.
    ///
    /// A band that never closes runs to the end of the session: the session stopped while suppressed, and
    /// drawing a point there would say capture came back, which is the one thing nobody knows.
    /// </summary>
    private IEnumerable<TimelineMarker> Bands(Session session)
    {
        long? from = null;
        CaptureStateReason? reason = null;

        foreach (var state in session.Events.OfType<CaptureStateEvent>().OrderBy(e => e.TsMs))
        {
            var off = state.State is CaptureState.Suppressed or CaptureState.Paused;
            if (off && from is null)
            {
                from = state.TsMs;
                reason = state.Reason;
            }
            else if (!off && from is { } start)
            {
                yield return Band(start, state.TsMs, reason);
                from = null;
                reason = null;
            }
        }

        if (from is { } open)
        {
            yield return Band(open, DurationMs, reason);
        }
    }

    private TimelineMarker Band(long from, long to, CaptureStateReason? reason)
    {
        var kind = reason == CaptureStateReason.OutOfScope
            ? TimelineMarkerKind.OutOfScope
            : TimelineMarkerKind.Suppressed;

        return new TimelineMarker(kind, from, to, from / (double)DurationMs, null, Describe(reason));
    }

    private TimelineMarker Point(TimelineMarkerKind kind, long tsMs, string? frameId, string? description = null) =>
        new(kind, tsMs, tsMs, Math.Clamp(tsMs / (double)DurationMs, 0, 1), frameId, description);

    /// <summary>
    /// Keeps at most <see cref="MaxClickMarkers"/> of them, evenly spread.
    ///
    /// Evenly rather than the first eighty: the last click of a session is usually the one that fixed it,
    /// and a strip that stops halfway through says the session did.
    /// </summary>
    private static IEnumerable<long> Thin(List<long> times)
    {
        if (times.Count <= MaxClickMarkers)
        {
            return times;
        }

        var step = times.Count / (double)MaxClickMarkers;
        return Enumerable.Range(0, MaxClickMarkers).Select(i => times[(int)(i * step)]);
    }

    /// <summary>The scope decision's own words, so the tooltip says why rather than that.</summary>
    private static string Describe(CaptureStateReason? reason) => reason switch
    {
        CaptureStateReason.PasswordField => "Capture paused — a password field had focus",
        CaptureStateReason.SensitiveContext => "Capture paused — the screen looked like a sign-in page",
        CaptureStateReason.ExcludedApp => "Not capturing — the app is on your excluded list",
        CaptureStateReason.OutOfScope => "Not capturing — this window is not part of the session",
        CaptureStateReason.ElevatedWindow => "Not capturing — an elevated window had focus",
        CaptureStateReason.User => "Paused by you",
        _ => "Capture paused",
    };
}

/// <summary>
/// Whether the panel is open, and how tall it is (Spec §5 S3).
///
/// Collapsed by default and <c>Alt+T</c> toggles it. The choice persists, because a technician who works
/// with the transcript open should not reopen it for every session — and the one who never uses it should
/// not have to close it either.
/// </summary>
public sealed class TimelinePanelState(bool expanded = false)
{
    public bool Expanded { get; private set; } = expanded;

    public double Height => Expanded ? SessionTimeline.ExpandedHeight : SessionTimeline.CollapsedHeight;

    public bool Toggle()
    {
        Expanded = !Expanded;
        return Expanded;
    }
}
