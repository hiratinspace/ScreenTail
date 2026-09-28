using ScreenTail.Core.Intel;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Intel;

/// <summary>
/// ST-065 AC3: when no model can be reached — local-only mode, or no backend configured — the session
/// is still drafted, on this device, from what it holds: one step per thing the technician said, each
/// pointing at the screenshot the aligner tied it to, or one per screenshot when nothing was said.
/// Nothing is invented (every reference names something in the session), everything is marked low
/// confidence and local, and the note says what it is. A worse note than the model's, and the reason
/// Review exists; better than "we couldn't draft this session" for a session the technician chose to
/// keep on the device.
/// </summary>
public sealed class FallbackDraftTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 28, 14, 2, 0, TimeSpan.Zero);

    [Fact]
    public void EachThingSaidBecomesAStepPointingAtItsScreenshot()
    {
        var session = Session(
            frames: [Frame("f1", 10_000), Frame("f2", 40_000)],
            transcript:
            [
                Said("t1", 9_500, "spooler service is stopped, that's why nothing prints", frameId: "f1"),
                Said("t2", 39_000, "started it and set it to automatic", frameId: "f2"),
                Said("t3", 55_000, "test page came out fine"),
            ]);

        var draft = FallbackDraft.Build(session);

        Assert.Equal(3, draft.Steps.Count);
        Assert.Equal("Spooler service is stopped, that's why nothing prints.", draft.Steps[0].Text);
        Assert.Equal(["f1"], draft.Steps[0].FrameRefs);
        Assert.Equal(["t1"], draft.Steps[0].TranscriptRefs);
        Assert.Empty(draft.Steps[2].FrameRefs);
        Assert.All(draft.Steps, s => Assert.Equal(StepConfidence.Low, s.Confidence));
        Assert.Equal(DraftSource.Local, draft.Source);
        Assert.Equal(FallbackDraft.PromptVersion, draft.PromptVersion);
    }

    [Fact]
    public void WithNothingSaidEachScreenshotIsAStepAndAMarkedOneSaysSo()
    {
        var session = Session(
            frames: [Frame("f1", 14_000), Frame("f2", 61_000)],
            events: [new MarkerEvent { TsMs = 61_000, FrameId = "f2" }]);

        var draft = FallbackDraft.Build(session);

        Assert.Equal(2, draft.Steps.Count);
        Assert.Equal("Screenshot at 0:14.", draft.Steps[0].Text);
        Assert.Equal(["f1"], draft.Steps[0].FrameRefs);
        Assert.Equal("Screenshot at 1:01, marked by you.", draft.Steps[1].Text);
    }

    [Fact]
    public void AReferenceNeverNamesAFrameTheSessionCannotShow()
    {
        // The eval harness's definition of a hallucination (ST-062), held by construction: a segment
        // the aligner tied to a frame that was never redacted, or was purged, gets no frame reference.
        var session = Session(
            frames: [Frame("pending", 10_000, pending: true)],
            transcript: [Said("t1", 9_500, "looking at the services list", frameId: "pending"), Said("t2", 20_000, "and this one", frameId: "gone")]);

        var draft = FallbackDraft.Build(session);

        Assert.All(draft.Steps, s => Assert.Empty(s.FrameRefs));
    }

    [Fact]
    public void TheNoteSaysWhatItIsAndWhatItDoesNotKnow()
    {
        var session = Session(frames: [Frame("f1", 10_000)], transcript: [Said("t1", 9_500, "the vpn drops every hour", frameId: "f1")], durationMs: 23 * 60_000);

        var draft = FallbackDraft.Build(session);

        Assert.Equal("The vpn drops every hour.", draft.Problem);
        Assert.Contains("not recorded", draft.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(draft.FollowUps);
        Assert.Equal(23, draft.SuggestedTimeMinutes);
        Assert.False(draft.KbCandidate);
        Assert.Contains("without a model", draft.KbReason, StringComparison.Ordinal);
        Assert.Contains("ScreenConnect", draft.SuggestedTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionWithNothingInItStillDraftsSomethingHonest()
    {
        var draft = FallbackDraft.Build(Session(frames: [], durationMs: 5_000));

        Assert.Empty(draft.Steps);
        Assert.NotEmpty(draft.Problem);
        Assert.Contains("nothing was captured", draft.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVeryLongSessionIsCappedAndSaysSo()
    {
        var frames = Enumerable.Range(0, 80).Select(i => Frame($"f{i}", i * 5_000L)).ToList();

        var draft = FallbackDraft.Build(Session(frames: frames));

        Assert.Equal(FallbackDraft.MaxSteps, draft.Steps.Count);
        Assert.Contains($"{80 - FallbackDraft.MaxSteps} more", draft.Result, StringComparison.Ordinal);
    }

    private static Session Session(IReadOnlyList<Frame> frames, IReadOnlyList<TranscriptSegment>? transcript = null, IReadOnlyList<SessionEvent>? events = null, long durationMs = 120_000) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s1",
        StartedAt = Started,
        DurationMs = durationMs,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Screenconnect },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = true,
        Events = events ?? [],
        Frames = frames,
        Transcript = transcript ?? [],
    };

    private static Frame Frame(string id, long tsMs, bool pending = false) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = "aW1hZ2U=",
        Width = 1920,
        Height = 1080,
        RedactionPending = pending,
        RedactedAt = pending ? null : Started,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    private static TranscriptSegment Said(string id, long tsMs, string text, string? frameId = null) => new()
    {
        Id = id,
        TsMs = tsMs,
        EndMs = tsMs + 2_000,
        Speaker = Speaker.Tech,
        Text = text,
        Confidence = 0.9,
        FrameId = frameId,
    };
}
