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

    /// <summary>Replaces the staged image with the redacted one and clears <c>redaction_pending</c>.</summary>
    /// <exception cref="InvalidOperationException">The frame doesn't exist or was already redacted.</exception>
    Task MarkFrameRedactedAsync(string frameId, RedactionOutcome outcome, CancellationToken ct = default);

    Task<int> CountPendingFramesAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the session's frames that are still pending (finalize grace expired), records the count on
    /// the session and in the audit log, and returns it.
    /// </summary>
    Task<int> PurgePendingFramesAsync(string sessionId, CancellationToken ct = default);

    Task SetFrameExcludedAsync(string frameId, bool excluded, CancellationToken ct = default);

    Task SaveDraftAsync(string sessionId, DraftNote draft, CancellationToken ct = default);

    Task FinalizeSessionAsync(string sessionId, FinalizeInfo info, CancellationToken ct = default);

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
