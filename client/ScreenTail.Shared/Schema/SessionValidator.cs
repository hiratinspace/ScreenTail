namespace ScreenTail.Shared.Schema;

/// <summary>
/// Rules the generated types can't express on their own: the ones JSON Schema states with if/then, plus
/// references and ordering, which JSON Schema can't state at all. Checked on the C# types so a session built
/// in memory obeys them just like one read from disk. Returns problems; never throws.
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
            switch (events[i])
            {
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
