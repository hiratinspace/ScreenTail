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
