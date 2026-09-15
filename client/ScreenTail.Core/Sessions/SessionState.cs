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
}

public static class SessionStateNames
{
    public const string Discarded = "discarded";

    /// <summary>
    /// The session reached a PSA. Spec §5 S4 filters history by it, so the history knows the name — but
    /// nothing writes it yet: publishing is ST-078, and this is the one place it will set.
    ///
    /// Declared here rather than as a literal in the history code so there is a single spelling to find,
    /// and so the gap is visible next to the state that does exist.
    /// </summary>
    public const string Published = "published";

    public static string ToWire(this SessionState state) => state switch
    {
        SessionState.Idle => CaptureStates.Idle,
        SessionState.Recording => CaptureStates.Recording,
        SessionState.Paused => CaptureStates.Paused,
        SessionState.Suppressed => CaptureStates.Suppressed,
        SessionState.Finalizing => CaptureStates.Finalizing,
        SessionState.DraftReady => CaptureStates.DraftReady,
        SessionState.DraftFailed => CaptureStates.DraftFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>States a session can be left in by a crash; recovery finalizes them (partial capture).</summary>
    public static IReadOnlyList<string> Orphanable { get; } =
        [CaptureStates.Recording, CaptureStates.Paused, CaptureStates.Suppressed, CaptureStates.Finalizing];
}
