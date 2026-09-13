using System.Windows;
using CoreShell = ScreenTail.Core.Shell;

namespace ScreenTail.UI.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
        : this(Sample)
    {
    }

    public DiagnosticsWindow(CoreShell.Diagnostics diagnostics)
    {
        InitializeComponent();
        DataContext = new DiagnosticsViewModel(diagnostics);
    }

    /// <summary>
    /// What the panel shows before anything has connected, and what the CI render draws. Every value is
    /// the shape of a real one so the screenshot shows the panel doing its job — a blank one would pass
    /// the render check while proving nothing.
    /// </summary>
    public static CoreShell.Diagnostics Sample { get; } = new(
        Scope: "Capturing — ScreenConnect",
        Microphone: "Headset Microphone (Realtek)",
        Suppression: null,
        RedactionBacklog: 4,
        FramesDropped: 0,
        KeystrokesDropped: 17,
        EgressBlocked: 0,
        LocalOnly: true,
        PolicyVersion: "policy-2026-09-13",
        CpuPercent: 3.4,
        WorkingSetBytes: 240L * 1024 * 1024,
        ClientVersion: "0.1.0");
}
