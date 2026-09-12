using System.Text.Json;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Schema;

/// <summary>The cross-field rules that the generated types alone can't enforce (ST-003).</summary>
public class SessionValidatorTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 10, 14, 2, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void WellFormedSessionHasNoProblems()
    {
        var session = Minimal(
            events: [new ClickEvent { TsMs = 10, X = 1, Y = 2, Button = MouseButton.Left, FrameId = "f1" }],
            frames: [Redacted("f1", 11)],
            transcript: [Segment("s1", 12, frameId: "f1")],
            draft: Draft(["f1"], ["s1"]));

        Assert.Empty(SessionValidator.Validate(session));
    }

    [Fact]
    public void WrongSchemaVersionIsAProblem()
    {
        var problems = SessionValidator.Validate(Minimal(version: "session.v2"));

        Assert.Contains(problems, p => p.StartsWith("schema_version", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingFrameWithOcrTextIsAProblem()
    {
        var frame = Pending("f1", 1) with { OcrText = "Card: 4111" };

        var problems = SessionValidator.Validate(Minimal(frames: [frame]));

        Assert.Single(problems, p => p.Contains("carries ocr_text (INV-1)", StringComparison.Ordinal));
    }

    [Fact]
    public void PendingFrameWithRedactedAtIsAProblem()
    {
        var frame = Pending("f1", 1) with { RedactedAt = At };

        Assert.Single(SessionValidator.Validate(Minimal(frames: [frame])), p => p.Contains("carries redacted_at", StringComparison.Ordinal));
    }

    [Fact]
    public void RedactedFrameWithoutRedactedAtIsAProblem()
    {
        var frame = Pending("f1", 1) with { RedactionPending = false };

        Assert.Single(SessionValidator.Validate(Minimal(frames: [frame])), p => p.Contains("no redacted_at", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateFrameIdIsAProblem()
    {
        Assert.Single(SessionValidator.Validate(Minimal(frames: [Redacted("f1", 1), Redacted("f1", 2)])), p => p.Contains("duplicate id", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfOrderTimestampsAreAProblem()
    {
        var session = Minimal(events: [new EnterEvent { TsMs = 20 }, new EnterEvent { TsMs = 10 }]);

        Assert.Single(SessionValidator.Validate(session), p => p.StartsWith("events[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void DanglingReferencesAreProblems()
    {
        var session = Minimal(
            events:
            [
                new ClickEvent { TsMs = 1, X = 0, Y = 0, Button = MouseButton.Left, FrameId = "nope" },
                new MarkerEvent { TsMs = 2, FrameId = "nope" },
                new NarrationEvent { TsMs = 3, SegmentId = "nope" },
            ],
            transcript: [Segment("s1", 4, frameId: "nope")],
            draft: Draft(["nope"], ["nope"]));

        var problems = SessionValidator.Validate(session);

        // click, marker, narration, transcript frame_id, draft frame_ref, draft transcript_ref
        Assert.Equal(6, problems.Count(p => p.Contains("does not exist", StringComparison.Ordinal)));
    }

    [Fact]
    public void SegmentEndingBeforeItStartsIsAProblem()
    {
        var segment = Segment("s1", 100) with { EndMs = 50 };

        Assert.Single(SessionValidator.Validate(Minimal(transcript: [segment])), p => p.Contains("end_ms", StringComparison.Ordinal));
    }

    [Fact]
    public void SerializeRefusesAnInvalidSession()
    {
        var invalid = Minimal(frames: [Pending("f1", 1) with { OcrText = "leak" }]);

        var ex = Assert.Throws<InvalidOperationException>(() => SessionJson.Serialize(invalid));

        Assert.Contains("INV-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeRejectsAnInvalidDocumentWithTheReason()
    {
        var json = JsonSerializer.Serialize(Minimal(version: "session.v9"), SessionJson.Options);

        var ex = Assert.Throws<JsonException>(() => SessionJson.Deserialize(json));

        Assert.Contains("schema_version", ex.Message, StringComparison.Ordinal);
    }

    private static Session Minimal(
        IReadOnlyList<SessionEvent>? events = null,
        IReadOnlyList<Frame>? frames = null,
        IReadOnlyList<TranscriptSegment>? transcript = null,
        DraftNote? draft = null,
        string version = SessionValidator.ExpectedSchemaVersion) => new()
        {
            SchemaVersion = version,
            SessionId = "test",
            StartedAt = At,
            RemoteTool = new RemoteTool { Kind = RemoteToolKind.Rdp },
            PartialCapture = false,
            FramesPurgedUnredacted = 0,
            LocalOnly = false,
            Events = events ?? [],
            Frames = frames ?? [],
            Transcript = transcript ?? [],
            Draft = draft,
        };

    private static Frame Pending(string id, long tsMs) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = $"frames/{id}.jpg",
        Width = 1600,
        Height = 900,
        RedactionPending = true,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    private static Frame Redacted(string id, long tsMs) => Pending(id, tsMs) with { RedactionPending = false, RedactedAt = At };

    private static TranscriptSegment Segment(string id, long tsMs, string? frameId = null) => new()
    {
        Id = id,
        TsMs = tsMs,
        EndMs = tsMs + 1000,
        Speaker = Speaker.Tech,
        Text = "hello",
        FrameId = frameId,
    };

    private static DraftNote Draft(IReadOnlyList<string> frameRefs, IReadOnlyList<string> transcriptRefs) => new()
    {
        Problem = "p",
        Steps = [new DraftStep { Text = "step", Confidence = StepConfidence.High, FrameRefs = frameRefs, TranscriptRefs = transcriptRefs }],
        Result = "r",
        FollowUps = [],
        SuggestedTitle = "t",
        SuggestedTimeMinutes = 15,
        KbCandidate = false,
        KbReason = "no",
        Source = DraftSource.Cloud,
        PromptVersion = "note_v1",
    };
}
