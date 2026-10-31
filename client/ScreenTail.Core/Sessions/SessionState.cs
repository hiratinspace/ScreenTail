using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Sessions;

/// <summary>The session lifecycle (ST-020). Wire names live in <see cref="CaptureStates"/>.</summary>
public enum SessionState
{
    Idle,
    Recording,
    Paused,
    Suppressed,
    Finalizing,
    DraftReady,
    DraftFailed,

    /// <summary>Finalized, its draft queued for the backend (ST-013). Captures nothing; rests until the outbox settles it.</summary>
    DraftPending,
}

public static class SessionStateNames
{
    public const string Discarded = "discarded";

    /// <summary>
    /// The session reached a PSA. Spec §5 S4 filters history by it. Written by the publish command once any
    /// destination landed (review of #243), never over a discarded row, and a named discard refuses a
    /// session in it: its note is in the PSA (owner decision, fail closed). A published session is no
    /// longer counted among the drafts ready, and the tray's Discard, which takes whatever the machine is
    /// resting on, is unchanged.
    ///
    /// Declared here rather than as a literal in the history code so there is a single spelling to find.
    /// </summary>
    public const string Published = "published";

    /// <summary>
    /// Finalized, with its draft in the outbox on the way to the backend (ST-013). The machine rests in
    /// draft_pending meanwhile, capturing nothing, and recovery adopts the newest such session again after
    /// a restart; the outbox moves the session on to draft_ready or draft_failed when the backend answers
    /// for good. Not orphanable: the queue still holds the work.
    /// </summary>
    public const string DraftPending = CaptureStates.DraftPending;

    public static string ToWire(this SessionState state) => state switch
    {
        SessionState.Idle => CaptureStates.Idle,
        SessionState.Recording => CaptureStates.Recording,
        SessionState.Paused => CaptureStates.Paused,
        SessionState.Suppressed => CaptureStates.Suppressed,
        SessionState.Finalizing => CaptureStates.Finalizing,
        SessionState.DraftReady => CaptureStates.DraftReady,
        SessionState.DraftFailed => CaptureStates.DraftFailed,
        SessionState.DraftPending => CaptureStates.DraftPending,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>States a session can be left in by a crash; recovery finalizes them (partial capture).</summary>
    public static IReadOnlyList<string> Orphanable { get; } =
        [CaptureStates.Recording, CaptureStates.Paused, CaptureStates.Suppressed, CaptureStates.Finalizing];
}
