using ScreenTail.Core.Detection;

namespace ScreenTail.Core.Capture;

/// <summary>
/// The one question every frame path asks before it points the camera (INV-5, INV-6): clicks, the scene
/// sampler and mark moment all ask it here, so the rule is one thing in one place.
/// </summary>
internal static class FrameGate
{
    /// <summary>
    /// The window a picture may be taken of, or null when none may.
    ///
    /// Null when nothing is known about the window in front, when the decision forbids frames (excluded,
    /// elevated, out of scope), and when a decision that allows frames names no window. The last cannot
    /// come from <see cref="ScopePolicy"/>, which always names the window it judged, but the capturer reads a
    /// zero window as "whatever is in front", so a decision without one would photograph a window nobody
    /// judged. That fails closed here rather than open in the capturer.
    /// </summary>
    public static nint? WindowFor(ScopeDecision? scope) =>
        scope is { MayCaptureFrames: true } && scope.Window != 0 ? scope.Window : null;
}
