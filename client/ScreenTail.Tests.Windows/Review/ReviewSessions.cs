using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>Schema-valid sessions, frames and drafts for the Review view-model tests, so each test states only what it is about.</summary>
internal static class ReviewSessions
{
    public static Session Session(params Frame[] frames) => Session(Draft(), frames);

    public static Session Session(DraftNote? draft, params Frame[] frames) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s1",
        StartedAt = DateTimeOffset.UnixEpoch,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Rdp },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = [],
        Transcript = [],
        Draft = draft,
        Frames = frames,
    };

    public static Frame Frame(string id, long tsMs, bool excluded = false, bool sensitive = false) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = $"frames/{id}.jpg",
        Width = 1600,
        Height = 900,
        RedactionPending = false,
        RedactedAt = DateTimeOffset.UnixEpoch,
        MaskedRegions = [],
        SensitiveContext = sensitive,
        ExcludedByUser = excluded,
    };

    public static Frame[] Frames(int count) => [.. Enumerable.Range(1, count).Select(i => Frame($"f{i}", i * 1000L))];

    public static DraftNote Draft(string problem = "The print spooler was stopped.", params string[] frameRefs) => new()
    {
        Problem = problem,
        Steps = [new DraftStep { Text = "Checked the spooler service.", Confidence = StepConfidence.High, FrameRefs = frameRefs }],
        Result = "Printing works again.",
        FollowUps = [],
        SuggestedTitle = "Printer offline",
        SuggestedTimeMinutes = 30,
        KbCandidate = false,
        KbReason = "Not a KB candidate: one-off fix",
        Source = DraftSource.Cloud,
        PromptVersion = "note_v1",
    };

    /// <summary>A 4x4 PNG, so a thumbnail can really be decoded.</summary>
    public static byte[] TinyImage()
    {
        using var bitmap = new System.Drawing.Bitmap(4, 4);
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        return buffer.ToArray();
    }

    /// <summary>
    /// A service that remembers every edit and every picture it was asked for. <see cref="Records"/> and
    /// <see cref="Deletes"/> play a service whose write failed; <see cref="Gate"/> holds every thumbnail
    /// request until the test lets it go, so something can happen while the strip is still loading.
    /// </summary>
    public sealed class RecordingFrames : IReviewFrames
    {
        public List<(string Id, bool Included)> Included { get; } = [];

        public List<string> Deleted { get; } = [];

        public List<string> ThumbnailsAsked { get; } = [];

        public List<string> ImagesAsked { get; } = [];

        public bool Records { get; set; } = true;

        public bool Deletes { get; set; } = true;

        public byte[]? Image { get; set; } = TinyImage();

        public TaskCompletionSource? Gate { get; set; }

        public async Task<byte[]?> ThumbnailAsync(Frame frame, CancellationToken ct = default)
        {
            ThumbnailsAsked.Add(frame.Id);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            return Image;
        }

        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default)
        {
            ImagesAsked.Add(frame.Id);
            return Task.FromResult(Image);
        }

        public Task<bool> SetIncludedAsync(string frameId, bool included, CancellationToken ct = default)
        {
            Included.Add((frameId, included));
            return Task.FromResult(Records);
        }

        /// <summary>When set, a delete waits for this answer: a service that is slow, or never answers.</summary>
        public TaskCompletionSource<bool>? DeleteAnswer { get; set; }

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default)
        {
            Deleted.Add(frameId);
            return DeleteAnswer?.Task ?? Task.FromResult(Deletes);
        }

        public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) => Task.FromResult(Image);
    }
}
