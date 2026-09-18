using ScreenTail.Core.Intel;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;

namespace ScreenTail.Service.Intel;

/// <summary>
/// Builds the session bundle when a session finishes, and reports that there is nobody to send it to
/// yet (ST-060).
///
/// The bundle is assembled for real on every session: the frames are selected, the transcript is aligned
/// to them, and the size and token estimate are logged (ST-060 AC3). Only the last step is missing, and
/// it is missing for a reason a technician can act on — no model provider is configured — rather than
/// because the code path does not exist.
///
/// Doing the work now rather than waiting for ST-063 is deliberate. It means the selection rules run
/// against real sessions on real hardware from today, so the numbers in the log are measurements rather
/// than estimates by the time there is a provider to send them to. It also means the failure a
/// technician sees is honest: Review shows "we could not draft this" with the reason, and the screenshots
/// and transcript are still there (Spec §5 S3).
/// </summary>
internal sealed partial class BundlingDrafter(
    ISessionStore store,
    ILogger logger,
    Core.Outbox.Outbox? outbox = null,
    BundleOptions? options = null) : IDrafter
{
    public const string NoProviderReason = "No summarization provider is configured, so this session could not be drafted.";

    /// <summary>The last bundle's shape, for the diagnostics panel. Counts only, never content (INV-10).</summary>
    public BundleReport? Last { get; private set; }

    public async Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = await store.LoadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return DraftOutcome.Failure("The session was gone before it could be drafted.");
        }

        var bundle = BundleBuilder.Build(session, options);
        Last = new BundleReport(
            bundle.FramesConsidered,
            bundle.Frames.Count,
            bundle.Transcript.Count,
            bundle.EstimatedBytes,
            bundle.EstimatedTokens,
            bundle.OcrPartial);

        // Counts and sizes only. Nothing here has been on a customer's screen (INV-10).
        LogBundle(
            logger,
            sessionId,
            bundle.FramesConsidered,
            bundle.Frames.Count,
            bundle.Transcript.Count,
            bundle.EstimatedBytes / 1024,
            bundle.EstimatedTokens,
            bundle.OcrPartial,
            bundle.FramesPurgedUnredacted);

        // ST-064: the work is queued before it is refused, so a draft owed on a train is still owed when
        // the machine comes back. The queue holds the request, not the bundle: frames are re-selected when
        // it is finally sent, from a store that retention may have thinned in the meantime, so a queued
        // draft can never resurrect a frame the tenant's window has already removed (INV-12).
        if (outbox is not null)
        {
            _ = await outbox.EnqueueAsync(
                new NewOutboxItem(sessionId, OutboxKind.Draft, $"draft:{sessionId}", "{}"),
                ct).ConfigureAwait(false);
        }

        return DraftOutcome.Failure(NoProviderReason);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Bundle for {SessionId}: {Chosen} of {Considered} frames, {Segments} transcript segments, {Kilobytes} KB, ~{Tokens} tokens, ocr_partial={OcrPartial}, {Purged} frames purged unredacted")]
    private static partial void LogBundle(
        ILogger logger,
        string sessionId,
        int considered,
        int chosen,
        int segments,
        long kilobytes,
        int tokens,
        bool ocrPartial,
        long purged);
}

/// <param name="Considered">Frames the session had before selection.</param>
/// <param name="Chosen">Frames that survived every exclusion and the budget.</param>
public sealed record BundleReport(
    int Considered,
    int Chosen,
    int Segments,
    long Bytes,
    int Tokens,
    bool OcrPartial);
