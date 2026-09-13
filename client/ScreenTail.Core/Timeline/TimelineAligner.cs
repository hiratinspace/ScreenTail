using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Timeline;

public sealed record AlignmentOptions
{
    /// <summary>
    /// How far a screenshot may be from what was said about it. ST-028 sets eight seconds: long enough to
    /// cover a technician acting and then explaining, short enough that a sentence does not get attached to
    /// a screenshot from a different step.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(8);
}

/// <param name="Segments">The same segments, each with a frame attached where one was found.</param>
/// <param name="Narration">Events for the segments that had no screenshot near them.</param>
public sealed record Alignment(IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<NarrationEvent> Narration)
{
    public int Linked => Segments.Count(s => s.FrameId is not null);
}

/// <summary>
/// Decides which screenshot each thing the technician said was about (ST-028).
///
/// This is what turns a list of pictures and a wall of text into a note somebody can follow: step three
/// shows the Services window <i>and</i> says "the spooler was stopped, so I started it". Without it, the
/// drafter gets frames and sentences with nothing joining them, and has to guess — which is exactly the
/// kind of guess that produces a confident, wrong ticket note.
///
/// <b>A segment that overlaps a frame is about that frame.</b> Speech and action overlap constantly: a
/// technician says "I'm restarting the spooler now" while clicking, so the sentence starts before the
/// screenshot and ends after it. Anchoring on the segment's start and looking backwards — the literal
/// reading of "nearest preceding frame" — would miss that entirely and send the most common case to
/// narration. So distance is measured from the segment's whole span: a frame inside it is zero away.
///
/// <b>Ties go to the earlier frame.</b> When a segment sits exactly between two screenshots, it belongs to
/// the one that already happened, because people describe what they did more often than what they are
/// about to do.
///
/// In Core rather than beside the capture code (ADR-0002): it is arithmetic over timestamps, it decides
/// what a published note says, and it should be arguable without a Windows machine.
/// </summary>
public static class TimelineAligner
{
    /// <param name="segments">What was said. Order does not matter; they are sorted here.</param>
    /// <param name="frames">The screenshots, with the session-relative time each was taken.</param>
    public static Alignment Align(
        IEnumerable<TranscriptSegment> segments,
        IEnumerable<Frame> frames,
        AlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(frames);
        var window = (long)(options ?? new AlignmentOptions()).Window.TotalMilliseconds;

        // Sorted so ties break the same way every run: a note that changes between two runs over the same
        // session is a note nobody can review.
        var ordered = frames.OrderBy(f => f.TsMs).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var aligned = new List<TranscriptSegment>();
        var narration = new List<NarrationEvent>();

        foreach (var segment in segments.OrderBy(s => s.TsMs).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var frame = Nearest(ordered, segment, window);
            aligned.Add(segment with { FrameId = frame?.Id });

            if (frame is null)
            {
                // Not dropped and not silently unattached: Spec §5 S3 shows these as narration, so a
                // technician's explanation of something they did not screenshot still reaches the note.
                narration.Add(new NarrationEvent { TsMs = segment.TsMs, SegmentId = segment.Id });
            }
        }

        return new Alignment(aligned, narration);
    }

    private static Frame? Nearest(List<Frame> frames, TranscriptSegment segment, long window)
    {
        Frame? best = null;
        var bestDistance = long.MaxValue;

        foreach (var frame in frames)
        {
            var distance = Distance(frame.TsMs, segment.TsMs, segment.EndMs);
            if (distance > window)
            {
                continue;
            }

            // Strictly nearer, so the first frame at a given distance wins — and because the list is
            // sorted by time, that is the earlier one.
            if (distance < bestDistance)
            {
                best = frame;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>How far a moment is from a span. Zero when it falls inside it.</summary>
    private static long Distance(long at, long from, long to)
    {
        if (at < from)
        {
            return from - at;
        }

        return at > to ? at - to : 0;
    }
}
