using System.Text.Json.Serialization;
using ScreenTail.Core.Timeline;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Intel;

/// <param name="MaxFrames">The ticket's ceiling. Twenty-five is what a vision model reads well.</param>
/// <param name="MaxBytes">
/// What the request may weigh. Twenty-five frames is a count, not a size: a request refused for being
/// too large costs the whole note, not a few pictures, so the byte budget bites first when it has to.
/// </param>
public sealed record BundleOptions
{
    public int MaxFrames { get; init; } = 25;

    public long MaxBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// Roughly what one image costs a vision model. Used only to report an estimate before the call, so
    /// a tenant's daily cost cap (ST-063) can be applied to a number rather than to a surprise.
    /// </summary>
    public int TokensPerFrame { get; init; } = 1_200;
}

/// <summary>
/// Everything the drafting model is given about one session, and nothing else (ST-060).
///
/// The shape is deliberate: no image is here that has not been read and masked, and no field carries a
/// window title, a keystroke or a file path. What this record contains is what leaves the machine.
/// </summary>
public sealed record SessionBundle
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("remote_tool")]
    public required RemoteTool RemoteTool { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    /// <summary>Capture started late or was interrupted, so the model should not claim to have seen it all.</summary>
    [JsonPropertyName("partial_capture")]
    public required bool PartialCapture { get; init; }

    /// <summary>
    /// Frames deleted because redaction could not finish (INV-1). The draft says so rather than leaving
    /// a silent gap, which is the difference between a note with a hole in it and a note that is wrong.
    /// </summary>
    [JsonPropertyName("frames_purged_unredacted")]
    public required long FramesPurgedUnredacted { get; init; }

    /// <summary>
    /// At least one included frame had no readable text. The prompt is allowed to hedge when it knows it
    /// is working from an incomplete picture, and cannot when nothing tells it.
    /// </summary>
    [JsonPropertyName("ocr_partial")]
    public required bool OcrPartial { get; init; }

    /// <summary>The timeline: clicks, typing counts, scene changes, markers. Never raw keystrokes (INV-2).</summary>
    [JsonPropertyName("events")]
    public required IReadOnlyList<SessionEvent> Events { get; init; }

    /// <summary>Redacted, unexcluded frames only, oldest first.</summary>
    [JsonPropertyName("frames")]
    public required IReadOnlyList<Frame> Frames { get; init; }

    /// <summary>What was said, each piece attached to the frame it was about where one survived selection.</summary>
    [JsonPropertyName("transcript")]
    public required IReadOnlyList<TranscriptSegment> Transcript { get; init; }

    /// <summary>How this MSP writes notes (ST-067). Empty until there are edits to learn from.</summary>
    [JsonPropertyName("style_hints")]
    public required IReadOnlyList<string> StyleHints { get; init; }

    // The three below never cross the wire: they are what the service logs and what a cost cap reads,
    // and they are not part of what the model is told. Deliberately not `required` — System.Text.Json
    // refuses a required property it has been told to ignore, and only BundleBuilder.Build sets them.

    /// <summary>How many frames the session had before selection. A count, for the log (INV-10).</summary>
    [JsonIgnore]
    public int FramesConsidered { get; init; }

    [JsonIgnore]
    public long EstimatedBytes { get; init; }

    /// <summary>Rough, and enough for a cost cap to act on before the call rather than a bill after it.</summary>
    [JsonIgnore]
    public int EstimatedTokens { get; init; }
}

/// <summary>
/// Turns a finished session into the payload the drafting model is given (ST-060).
///
/// <b>This is the egress boundary.</b> Everything upstream decides what is captured; this decides what
/// leaves the machine, and it is the last place any of it can be stopped. Four kinds of frame never
/// appear, and each is a separate rule with its own test:
///
/// <list type="bullet">
/// <item><b>Pending</b> — nobody has read it, so nobody can say what is on it (INV-1).</item>
/// <item><b>Sensitive context</b> — it looked like a sign-in screen. Redaction masked what it found, and
/// what it found is not the same as what was there.</item>
/// <item><b>Captured while paused or suppressed</b> — INV-6 says such a frame should not exist. If one
/// does, it is the most dangerous frame in the session, because something was suppressing capture when
/// it was taken.</item>
/// <item><b>Excluded in Review</b> — the technician looked at it and said no (ST-075).</item>
/// </list>
///
/// Selection spends a budget rather than taking the first twenty-five: a note built from the opening
/// clicks describes someone opening a console, and what they did at the end is usually the fix. Frames
/// are scored on whether anyone could read them and whether the technician talked over them, then spread
/// across the session so the whole of it is represented.
///
/// In Core rather than beside the service (ADR-0002): it is arithmetic over a session record, it decides
/// what a customer's ticket note is built from, and it should be arguable without a Windows machine.
/// </summary>
public static class BundleBuilder
{
    public static SessionBundle Build(Session session, BundleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var budget = options ?? new BundleOptions();

        var blocked = SuppressedIntervals(session.Events);
        var eligible = session.Frames
            .Where(frame => !frame.RedactionPending)
            .Where(frame => !frame.SensitiveContext)
            .Where(frame => !frame.ExcludedByUser)
            .Where(frame => !blocked.Any(interval => interval.Covers(frame.TsMs)))

            // Sorted before scoring so every tie breaks the same way: a bundle that varies between runs
            // makes a draft that varies between runs, and nobody can review a note that keeps changing.
            .OrderBy(frame => frame.TsMs)
            .ThenBy(frame => frame.Id, StringComparer.Ordinal)
            .ToList();

        var chosen = Select(eligible, session.Transcript, budget);

        // Alignment runs against what survived, not against everything: a sentence pointing at a frame
        // that was dropped is a dangling reference, and the note prompt rejects the whole draft over one.
        var alignment = TimelineAligner.Align(session.Transcript, chosen);

        var bytes = chosen.Sum(frame => (long)frame.Image.Length)
            + alignment.Segments.Sum(segment => (long)segment.Text.Length)
            + chosen.Sum(frame => (long)(frame.OcrText?.Length ?? 0));

        var text = alignment.Segments.Sum(segment => segment.Text.Length)
            + chosen.Sum(frame => frame.OcrText?.Length ?? 0);

        return new SessionBundle
        {
            SessionId = session.SessionId,
            RemoteTool = session.RemoteTool,
            DurationMs = session.DurationMs,
            PartialCapture = session.PartialCapture,
            FramesPurgedUnredacted = session.FramesPurgedUnredacted,
            OcrPartial = chosen.Count < eligible.Count || chosen.Exists(frame => string.IsNullOrWhiteSpace(frame.OcrText)),
            Events = session.Events,
            Frames = chosen,
            Transcript = alignment.Segments,
            StyleHints = [],
            FramesConsidered = session.Frames.Count,
            EstimatedBytes = bytes,

            // Four characters to a token is the usual rule of thumb for English prose, and an image is
            // priced flat. Both are estimates and are labelled as such; the point is a number a cost cap
            // can act on before the call rather than a bill after it.
            EstimatedTokens = (text / 4) + (chosen.Count * budget.TokensPerFrame),
        };
    }

    /// <summary>
    /// Picks the frames worth spending the budget on, oldest first.
    ///
    /// Two passes. The first walks the session in equal slices and takes the best frame from each, so the
    /// end of a session is represented as well as the beginning. The second spends whatever is left on
    /// the best of the rest. Both stop at the byte budget.
    /// </summary>
    private static List<Frame> Select(List<Frame> eligible, IReadOnlyList<TranscriptSegment> transcript, BundleOptions budget)
    {
        if (eligible.Count <= budget.MaxFrames && eligible.Sum(f => (long)f.Image.Length) <= budget.MaxBytes)
        {
            return eligible;
        }

        var scores = eligible.ToDictionary(frame => frame.Id, frame => Score(frame, transcript), StringComparer.Ordinal);
        var chosen = new List<Frame>();
        long bytes = 0;

        bool TryTake(Frame frame)
        {
            if (chosen.Count >= budget.MaxFrames || bytes + frame.Image.Length > budget.MaxBytes)
            {
                return false;
            }

            chosen.Add(frame);
            bytes += frame.Image.Length;
            return true;
        }

        // One slice per frame we are allowed, so the chosen frames are spread over the session's whole
        // length rather than clustered wherever the technician clicked fastest.
        var first = eligible[0].TsMs;
        var span = Math.Max(1, eligible[^1].TsMs - first);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (var slice = 0; slice < budget.MaxFrames; slice++)
        {
            var from = first + (span * slice / budget.MaxFrames);
            var to = first + (span * (slice + 1) / budget.MaxFrames);
            var best = eligible
                .Where(frame => frame.TsMs >= from && (frame.TsMs < to || (slice == budget.MaxFrames - 1 && frame.TsMs <= to)))
                .Where(frame => !taken.Contains(frame.Id))
                .OrderByDescending(frame => scores[frame.Id])
                .ThenBy(frame => frame.TsMs)
                .ThenBy(frame => frame.Id, StringComparer.Ordinal)
                .FirstOrDefault();

            if (best is not null && TryTake(best))
            {
                _ = taken.Add(best.Id);
            }
        }

        foreach (var frame in eligible
            .Where(frame => !taken.Contains(frame.Id))
            .OrderByDescending(frame => scores[frame.Id])
            .ThenBy(frame => frame.TsMs)
            .ThenBy(frame => frame.Id, StringComparer.Ordinal))
        {
            if (!TryTake(frame))
            {
                break;
            }

            _ = taken.Add(frame.Id);
        }

        return [.. chosen.OrderBy(frame => frame.TsMs).ThenBy(frame => frame.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// How much a frame is worth to someone writing the note.
    ///
    /// Talked over beats readable beats clicked, in that order. Speech is the strongest signal there is
    /// that a screenshot mattered — the technician stopped to explain it — and a frame nobody could read
    /// any words on tells a drafting model almost nothing.
    /// </summary>
    private static int Score(Frame frame, IReadOnlyList<TranscriptSegment> transcript)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(frame.OcrText))
        {
            score += 10;

            // More text is usually a dialog or a list rather than a desktop, capped so a wall of log
            // output cannot outweigh everything else in the session.
            score += Math.Min(10, frame.OcrText!.Length / 80);
        }

        if (transcript.Any(segment => Overlaps(segment, frame.TsMs)))
        {
            score += 20;
        }

        // A click is something the technician did; a scene change is something that happened to them.
        // Both matter, and the deliberate one slightly more.
        if (frame.Trigger == FrameTrigger.Click)
        {
            score += 3;
        }
        else if (frame.Trigger == FrameTrigger.Marker)
        {
            // "Mark this moment" is the only frame anyone asked for by name.
            score += 30;
        }

        return score;
    }

    private static bool Overlaps(TranscriptSegment segment, long tsMs) =>
        tsMs >= segment.TsMs - 8_000 && tsMs <= segment.EndMs + 8_000;

    /// <summary>
    /// The stretches where capture was off.
    ///
    /// An interval that never closes stays open to the end of the session on purpose: "we never heard it
    /// resume" is not the same as "it resumed", and only one of those readings is safe.
    /// </summary>
    private static List<Interval> SuppressedIntervals(IReadOnlyList<SessionEvent> events)
    {
        var intervals = new List<Interval>();
        long? openedAt = null;

        foreach (var state in events.OfType<CaptureStateEvent>().OrderBy(e => e.TsMs))
        {
            var off = state.State is CaptureState.Suppressed or CaptureState.Paused;
            if (off && openedAt is null)
            {
                openedAt = state.TsMs;
            }
            else if (!off && openedAt is { } from)
            {
                intervals.Add(new Interval(from, state.TsMs));
                openedAt = null;
            }
        }

        if (openedAt is { } last)
        {
            intervals.Add(new Interval(last, long.MaxValue));
        }

        return intervals;
    }

    private readonly record struct Interval(long From, long To)
    {
        public bool Covers(long tsMs) => tsMs >= From && tsMs <= To;
    }
}
