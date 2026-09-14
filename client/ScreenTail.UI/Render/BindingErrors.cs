using System.Diagnostics;

namespace ScreenTail.UI.Render;

/// <summary>
/// Makes WPF's silent binding failures loud, for the screenshot renders only.
///
/// A binding to a property that does not exist does not throw and does not stop the layout: it writes a
/// line to a trace source nobody reads and leaves the control blank. That is the one failure the
/// screenshot check cannot see, because a pane missing one label still has a background, borders and
/// every other label on it, and so still counts hundreds of distinct colours. Renaming a view-model
/// property would go green.
///
/// So the render collects them and the harness fails if there are any. Only in the render: in the app a
/// binding error must never take the window down in front of a technician, and by then the screenshots
/// have already refused to let one through.
/// </summary>
internal sealed class BindingErrors : TraceListener
{
    private static readonly List<string> Seen = [];

    private BindingErrors()
    {
    }

    public static IReadOnlyList<string> Collected
    {
        get
        {
            lock (Seen)
            {
                return [.. Seen];
            }
        }
    }

    public static void Listen()
    {
        // Error only. WPF logs a great deal at Warning that is ordinary — a binding resolving late during
        // template application, for one — and a check that fires on those would be turned off within a week.
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrors());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    public override void Write(string? message) => Record(message);

    public override void WriteLine(string? message) => Record(message);

    private static void Record(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (Seen)
        {
            Seen.Add(message);
        }
    }
}
