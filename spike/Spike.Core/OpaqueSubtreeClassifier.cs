namespace ScreenTail.Spike.Core;

public enum SubtreeKind
{
    Opaque,
    Accessible,
}

/// <param name="FocusedControlType">FlaUI <c>ControlType</c> name, e.g. "Pane", "Edit".</param>
/// <param name="FocusedAreaFraction">Focused element bounds ÷ top-level window bounds, 0..1.</param>
public sealed record UiaFocusSnapshot(
    string WindowClass,
    string FocusedClass,
    string FocusedControlType,
    int FocusedChildCount,
    double FocusedAreaFraction);

public sealed record SubtreeVerdict(SubtreeKind Kind, string Reason);

/// <summary>
/// Decides whether the focused UIA element is a remote canvas we cannot see inside.
/// The class list is a starting point; the spike run on real RDP confirms or corrects it.
/// </summary>
public static class OpaqueSubtreeClassifier
{
    public const double LargeElementFraction = 0.6;

    private static readonly HashSet<string> KnownRemoteCanvasClasses = new(StringComparer.Ordinal)
    {
        "IHWindowClass", // mstsc input-handler canvas
    };

    private static readonly HashSet<string> ContainerControlTypes = new(StringComparer.Ordinal)
    {
        "Pane",
        "Custom",
        "Window",
        "Unknown",
    };

    public static SubtreeVerdict Classify(UiaFocusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (KnownRemoteCanvasClasses.Contains(snapshot.FocusedClass))
        {
            return new SubtreeVerdict(SubtreeKind.Opaque, $"known remote canvas class {snapshot.FocusedClass}");
        }

        if (snapshot.FocusedChildCount == 0
            && snapshot.FocusedAreaFraction >= LargeElementFraction
            && ContainerControlTypes.Contains(snapshot.FocusedControlType))
        {
            return new SubtreeVerdict(
                SubtreeKind.Opaque,
                $"childless {snapshot.FocusedControlType} covering {snapshot.FocusedAreaFraction:P0} of window");
        }

        return new SubtreeVerdict(SubtreeKind.Accessible, $"{snapshot.FocusedChildCount} children, {snapshot.FocusedControlType}");
    }
}
