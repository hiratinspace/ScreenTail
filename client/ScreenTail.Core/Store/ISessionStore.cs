using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Store;

/// <summary>
/// The encrypted local store (ST-005). Frames enter with <c>redaction_pending</c> set and only the
/// redaction worker can read one back (<see cref="TakeNextPendingFrameAsync"/>). Everything else — Review,
/// bundle builder, export — reads through <see cref="LoadSessionAsync"/> and
/// <see cref="GetRedactedFrameImageAsync"/>, which return redacted frames only (INV-1).
/// </summary>
public interface ISessionStore : IAsyncDisposable
{
    Task CreateSessionAsync(NewSession session, CancellationToken ct = default);

    Task AppendEventAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default);

    Task AppendTranscriptAsync(string sessionId, TranscriptSegment segment, CancellationToken ct = default);

    /// <summary>Writes a freshly captured frame with <c>redaction_pending = 1</c>.</summary>
    Task StageFrameAsync(string sessionId, StagedFrame frame, CancellationToken ct = default);

    /// <summary>For the redaction worker only: the oldest frame still waiting for redaction, with its image.</summary>
    Task<PendingFrame?> TakeNextPendingFrameAsync(CancellationToken ct = default);

    /// <summary>
    /// The same, skipping frames another worker already has in hand. A frame stays
    /// <c>redaction_pending</c> until its redaction finishes, so the query alone cannot tell "waiting"
    /// from "being worked on" and two workers would take the same row.
    /// </summary>
    Task<PendingFrame?> TakeNextPendingFrameAsync(IReadOnlySet<string>? except, CancellationToken ct = default);

    /// <summary>Replaces the staged image with the redacted one and clears <c>redaction_pending</c>.</summary>
    /// <exception cref="InvalidOperationException">The frame doesn't exist or was already redacted.</exception>
    Task MarkFrameRedactedAsync(string frameId, RedactionOutcome outcome, CancellationToken ct = default);

    Task<int> CountPendingFramesAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Frames waiting for redaction across every session — the backlog depth the HUD shows (ST-041).</summary>
    Task<int> CountAllPendingFramesAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes one frame that could not be redacted, counting it against its session so Review can say a
    /// screenshot was dropped rather than leaving a silent gap. Used by the redaction worker when a frame
    /// cannot be read, masked, or fully searched — an unreadable frame kept is an unredacted frame.
    /// </summary>
    Task DiscardPendingFrameAsync(string frameId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the session's frames that are still pending (finalize grace expired), records the count on
    /// the session and in the audit log, and returns it.
    /// </summary>
    Task<int> PurgePendingFramesAsync(string sessionId, CancellationToken ct = default);

    Task SetFrameExcludedAsync(string frameId, bool excluded, CancellationToken ct = default);

    /// <summary>
    /// Deletes one screenshot for good (ST-075) and writes <see cref="AuditTypes.FrameDeletedByUser"/>.
    /// Called when the 5 s undo window closes, never before it — Spec §4 prefers an undo to a dialog, and
    /// an undo that cannot put the frame back is a dialog with extra steps.
    /// </summary>
    /// <returns>Whether there was a frame to delete.</returns>
    Task<bool> DeleteFrameAsync(string frameId, CancellationToken ct = default);

    /// <summary>
    /// Replaces a redacted frame's image with one the technician blurred a region out of, and records the
    /// region as <c>user_blur</c> (ST-075).
    ///
    /// The original bytes are not kept anywhere. Spec §5 S3 calls this destructive and means it: a
    /// technician blurring a password on a customer's screen is telling the product to destroy those
    /// pixels, and a copy retained "for undo" is a copy.
    /// </summary>
    /// <exception cref="InvalidOperationException">The frame is gone, or still pending redaction.</exception>
    Task ApplyUserBlurAsync(string frameId, ReadOnlyMemory<byte> image, MaskedRegion region, CancellationToken ct = default);

    Task SaveDraftAsync(string sessionId, DraftNote draft, CancellationToken ct = default);

    Task FinalizeSessionAsync(string sessionId, FinalizeInfo info, CancellationToken ct = default);

    /// <summary>Persists a state-machine transition (ST-020). <paramref name="state"/> is a wire name from <c>CaptureStates</c>.</summary>
    Task SetSessionStateAsync(string sessionId, string state, string? reason, CancellationToken ct = default);

    /// <summary>Sessions currently in any of the given states, oldest first. Used for orphan recovery and draft counts.</summary>
    Task<IReadOnlyList<string>> ListSessionsInStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default);

    /// <summary>The latest timestamp any event, frame or transcript segment carries, or 0 for an empty session.</summary>
    Task<long> GetLastTimestampAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Sessions whose raw data is still present and whose last activity — <c>ended_at</c>, or the creation
    /// time when a session never finished — is older than <paramref name="cutoff"/> (ST-044). A session that
    /// never reaches finalize ages out like any other; only <paramref name="activeSessionId"/> is exempt.
    /// </summary>
    Task<IReadOnlyList<string>> ListSessionsWithRawDataOlderThanAsync(DateTimeOffset cutoff, string? activeSessionId = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes the session's frames (images and OCR text), transcript and events; keeps the session row, its
    /// draft note and the audit log (INV-12). Returns the number of frames removed.
    /// </summary>
    Task<int> PurgeRawDataAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Review's Discard (ST-074, Spec §5 S3). Deletes the frames, transcript and events, drops the drafted
    /// note, marks the session discarded and writes the audit row — in one transaction, so there is no
    /// moment where the raw data is gone and nothing says why.
    ///
    /// Not <see cref="DeleteSessionAsync"/>, which removes the row: Spec §5 S4 lists Discarded as a status
    /// a technician can filter history by, and a row that is gone cannot have one. Not
    /// <see cref="PurgeRawDataAsync"/> either, which keeps the note on purpose, because retention ages out
    /// the evidence and leaves the deliverable — and here the note is the thing being thrown away.
    /// </summary>
    /// <returns>How many frames were deleted.</returns>
    Task<int> DiscardSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Rebuilds the database file so purged space is actually released.</summary>
    Task VacuumAsync(CancellationToken ct = default);

    /// <summary>
    /// The session as a schema document containing redacted frames only. References to frames that are
    /// pending or purged are dropped, so the result always passes <see cref="SessionValidator"/>.
    /// </summary>
    Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Image bytes of a redacted frame; null when the frame is pending or doesn't exist.</summary>
    Task<byte[]?> GetRedactedFrameImageAsync(string frameId, CancellationToken ct = default);

    /// <summary>Deletes the session and everything under it, and writes an audit row.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string? sessionId = null, CancellationToken ct = default);

    Task<int> GetSchemaVersionAsync(CancellationToken ct = default);
}

public sealed record NewSession(
    string SessionId,
    DateTimeOffset StartedAt,
    RemoteTool RemoteTool,
    bool LocalOnly,
    string? PolicyVersion);

/// <summary>A captured, not yet redacted frame. Never leaves the capture service unredacted (INV-1).</summary>
public sealed record StagedFrame(
    string Id,
    long TsMs,
    FrameTrigger Trigger,
    int Width,
    int Height,
    Point? Cursor,
    ReadOnlyMemory<byte> Image);

public sealed record PendingFrame(string Id, string SessionId, long TsMs, byte[] Image);

public sealed record RedactionOutcome(
    ReadOnlyMemory<byte> RedactedImage,
    string? OcrText,
    IReadOnlyList<MaskedRegion> MaskedRegions,
    bool SensitiveContext,
    DateTimeOffset RedactedAt);

public sealed record FinalizeInfo(long DurationMs, bool PartialCapture);

public sealed record AuditEntry(long Id, DateTimeOffset At, string? SessionId, string Type, long? Count);

/// <summary>Audit row types written by the store itself. Counts and states only (INV-10).</summary>
public static class AuditTypes
{
    public const string FramesPurgedUnredacted = "frames_purged_unredacted";
    public const string SessionDiscarded = "session_discarded";
    public const string RetentionPurged = "retention_purged";

    /// <summary>ST-075: the technician deleted one screenshot in Review, after the undo window closed.</summary>
    public const string FrameDeletedByUser = "frame_deleted_by_user";
}

/// <summary>The database could not be opened with the supplied key.</summary>
public sealed class StoreKeyException(string message, Exception inner) : Exception(message, inner)
{
    public StoreKeyException()
        : this("The store could not be opened with this key.", new InvalidOperationException())
    {
    }

    public StoreKeyException(string message)
        : this(message, new InvalidOperationException())
    {
    }
}
