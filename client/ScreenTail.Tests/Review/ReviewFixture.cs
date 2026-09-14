using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Review;

/// <summary>Minimal schema-valid values for the note-editor tests, so each test states only what it is about.</summary>
internal static class ReviewFixture
{
    public static DraftStep Step(string text, StepConfidence confidence = StepConfidence.High, params string[] frames) => new()
    {
        Text = text,
        Confidence = confidence,
        FrameRefs = frames,
    };

    public static DraftNote Draft(params DraftStep[] steps) => new()
    {
        Problem = "The print spooler was stopped.",
        Steps = steps.Length == 0 ? [Step("Checked the spooler service.")] : steps,
        Result = "Printing works again.",
        FollowUps = [],
        SuggestedTitle = "Printer offline — Acme Dental",
        SuggestedTimeMinutes = 30,
        KbCandidate = false,
        KbReason = "Not a KB candidate: one-off fix",
        Source = DraftSource.Cloud,
        PromptVersion = "note_v1",
    };

    public static Session Session(
        bool partialCapture = false,
        long framesPurged = 0,
        bool localOnly = false,
        DraftNote? draft = null) => new()
        {
            SchemaVersion = "session.v1",
            SessionId = "s-1",
            StartedAt = DateTimeOffset.UnixEpoch,
            RemoteTool = new RemoteTool { Kind = RemoteToolKind.Screenconnect },
            PartialCapture = partialCapture,
            FramesPurgedUnredacted = framesPurged,
            LocalOnly = localOnly,
            Events = [],
            Frames = [],
            Transcript = [],
            Draft = draft,
        };
}
