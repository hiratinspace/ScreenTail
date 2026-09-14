namespace ScreenTail.Shared.Schema;

/// <summary>
/// Rules the generated types can't express on their own: the ones JSON Schema states with if/then, plus
/// references and ordering, which JSON Schema can't state at all. Checked on the C# types so a session built
/// in memory obeys them just like one read from disk. Returns problems; never throws.
///
/// It also mirrors every <c>minimum</c> and <c>minLength</c> in session.v1.json. Those were left to the
/// schema on the argument that the validator handled what the schema could not — but the C# side is the
/// only gate on a session built in memory, so the two disagreed about what is valid: the client could
/// write a frame with an empty id or a typing burst of zero characters, and ajv on the web side would
/// then reject the file the client had just declared good. <see cref="Bounds"/> keeps the lists together
/// so a new constraint in the schema has an obvious counterpart here.
/// </summary>
public static class SessionValidator
{
    public const string ExpectedSchemaVersion = "session.v1";

    public static IReadOnlyList<string> Validate(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var problems = new List<string>();

        if (session.SchemaVersion != ExpectedSchemaVersion)
        {
            problems.Add($"schema_version: expected '{ExpectedSchemaVersion}', got '{session.SchemaVersion}'");
        }

        Bounds.Text("session_id", session.SessionId, problems);
        if (session.DurationMs is { } duration)
        {
            Bounds.AtLeast("duration_ms", duration, 0, problems);
        }

        Bounds.AtLeast("frames_purged_unredacted", session.FramesPurgedUnredacted, 0, problems);

        var frameIds = CheckFrames(session.Frames, problems);
        var segmentIds = CheckTranscript(session.Transcript, frameIds, problems);
        CheckEvents(session.Events, frameIds, segmentIds, problems);
        if (session.Draft is not null)
        {
            CheckDraft(session.Draft, frameIds, segmentIds, problems);
        }

        return problems;
    }

    private static HashSet<string> CheckFrames(IReadOnlyList<Frame> frames, List<string> problems)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var where = $"frames[{i}] '{frame.Id}'";
            if (!ids.Add(frame.Id))
            {
                problems.Add($"{where}: duplicate id");
            }

            Bounds.Text($"{where}.id", frame.Id, problems);
            Bounds.Text($"{where}.image", frame.Image, problems);
            Bounds.AtLeast($"{where}.ts_ms", frame.TsMs, 0, problems);
            Bounds.AtLeast($"{where}.width", frame.Width, 1, problems);
            Bounds.AtLeast($"{where}.height", frame.Height, 1, problems);

            for (var r = 0; r < frame.MaskedRegions.Count; r++)
            {
                var region = frame.MaskedRegions[r];
                var at = $"{where}.masked_regions[{r}]";
                Bounds.AtLeast($"{at}.x", region.X, 0, problems);
                Bounds.AtLeast($"{at}.y", region.Y, 0, problems);
                Bounds.AtLeast($"{at}.width", region.Width, 1, problems);
                Bounds.AtLeast($"{at}.height", region.Height, 1, problems);
            }

            // INV-1: nothing readable exists for a frame until the redaction worker has finished with it.
            if (frame.RedactionPending)
            {
                if (frame.OcrText is not null)
                {
                    problems.Add($"{where}: redaction_pending frame carries ocr_text (INV-1)");
                }

                if (frame.RedactedAt is not null)
                {
                    problems.Add($"{where}: redaction_pending frame carries redacted_at (INV-1)");
                }
            }
            else if (frame.RedactedAt is null)
            {
                problems.Add($"{where}: redacted frame has no redacted_at");
            }
        }

        CheckOrdered("frames", frames.Select(f => f.TsMs), problems);
        return ids;
    }

    private static HashSet<string> CheckTranscript(IReadOnlyList<TranscriptSegment> transcript, HashSet<string> frameIds, List<string> problems)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < transcript.Count; i++)
        {
            var segment = transcript[i];
            var where = $"transcript[{i}] '{segment.Id}'";
            if (!ids.Add(segment.Id))
            {
                problems.Add($"{where}: duplicate id");
            }

            Bounds.Text($"{where}.id", segment.Id, problems);
            Bounds.AtLeast($"{where}.ts_ms", segment.TsMs, 0, problems);
            if (segment.Confidence is { } confidence && confidence is < 0 or > 1)
            {
                problems.Add($"{where}: confidence {confidence} is outside 0-1");
            }

            if (segment.EndMs < segment.TsMs)
            {
                problems.Add($"{where}: end_ms {segment.EndMs} is before ts_ms {segment.TsMs}");
            }

            if (segment.FrameId is not null && !frameIds.Contains(segment.FrameId))
            {
                problems.Add($"{where}: frame_id '{segment.FrameId}' does not exist");
            }
        }

        CheckOrdered("transcript", transcript.Select(s => s.TsMs), problems);
        return ids;
    }

    private static void CheckEvents(IReadOnlyList<SessionEvent> events, HashSet<string> frameIds, HashSet<string> segmentIds, List<string> problems)
    {
        for (var i = 0; i < events.Count; i++)
        {
            var where = $"events[{i}]";
            Bounds.AtLeast($"{where}.ts_ms", events[i].TsMs, 0, problems);
            switch (events[i])
            {
                case TypingBurstEvent burst when burst.CharCount < 1:
                    problems.Add($"{where} typing_burst: char_count {burst.CharCount} is below the minimum of 1");
                    break;
                case FocusEvent { Process.Length: 0 }:
                    problems.Add($"{where} focus: process is empty (minLength 1)");
                    break;
                case ClickEvent { FrameId: { } frameId } when !frameIds.Contains(frameId):
                    problems.Add($"{where} click: frame_id '{frameId}' does not exist");
                    break;
                case MarkerEvent { FrameId: { } frameId } when !frameIds.Contains(frameId):
                    problems.Add($"{where} marker: frame_id '{frameId}' does not exist");
                    break;
                case NarrationEvent narration when !segmentIds.Contains(narration.SegmentId):
                    problems.Add($"{where} narration: segment_id '{narration.SegmentId}' does not exist");
                    break;
                default:
                    break;
            }
        }

        CheckOrdered("events", events.Select(e => e.TsMs), problems);
    }

    private static void CheckDraft(DraftNote draft, HashSet<string> frameIds, HashSet<string> segmentIds, List<string> problems)
    {
        for (var i = 0; i < draft.Steps.Count; i++)
        {
            var step = draft.Steps[i];
            foreach (var frameRef in step.FrameRefs.Where(r => !frameIds.Contains(r)))
            {
                problems.Add($"draft.steps[{i}]: frame_ref '{frameRef}' does not exist");
            }

            foreach (var segmentRef in (step.TranscriptRefs ?? []).Where(r => !segmentIds.Contains(r)))
            {
                problems.Add($"draft.steps[{i}]: transcript_ref '{segmentRef}' does not exist");
            }
        }
    }

    /// <summary>
    /// The <c>minimum</c> and <c>minLength</c> keywords from session.v1.json, in one place so the two
    /// sides cannot drift apart quietly. A schema keyword with no counterpart here is a rule the web
    /// enforces and the client does not.
    /// </summary>
    private static class Bounds
    {
        public static void Text(string where, string value, List<string> problems)
        {
            if (string.IsNullOrEmpty(value))
            {
                problems.Add($"{where}: is empty (minLength 1)");
            }
        }

        public static void AtLeast(string where, long value, long minimum, List<string> problems)
        {
            if (value < minimum)
            {
                problems.Add($"{where}: {value} is below the minimum of {minimum}");
            }
        }
    }

    private static void CheckOrdered(string list, IEnumerable<long> timestamps, List<string> problems)
    {
        long previous = long.MinValue;
        var index = 0;
        foreach (var ts in timestamps)
        {
            if (ts < previous)
            {
                problems.Add($"{list}[{index}]: ts_ms {ts} is earlier than the previous item's {previous}");
            }

            previous = ts;
            index++;
        }
    }
}
