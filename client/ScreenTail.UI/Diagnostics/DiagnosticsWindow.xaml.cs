using System.Windows;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using CoreShell = ScreenTail.Core.Shell;

namespace ScreenTail.UI.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    /// <summary>
    /// The CI render only. A panel showing a literal is what this window used to do in production, and a
    /// panel that tells a customer "local-only: yes" from a constant is worse than no panel at all
    /// (weaknesses P1-2). <see cref="ShowLiveAsync"/> is what the application uses.
    /// </summary>
    public DiagnosticsWindow()
        : this(Sample)
    {
    }

    /// <summary>
    /// Opens the panel with what the service says, or saying unknown on every row when it cannot be asked
    /// (ST-085, T-152).
    ///
    /// The question is asked every time the panel opens rather than cached, because the answer is what a
    /// technician turns the screen round and shows a customer, and a stale one is a claim about the
    /// present tense that happens to be about the past.
    /// </summary>
    public static async Task ShowLiveAsync(CaptureConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var reported = await connection.RequestAsync<DiagnosticsReported>(
            id => new GetDiagnosticsCommand { RequestId = id }).ConfigureAwait(true);

        var window = new DiagnosticsWindow(reported is null ? CoreShell.DiagnosticsPanel.Unanswered : CoreShell.DiagnosticsPanel.From(From(reported)));
        window.Show();
        _ = window.Activate();
    }

    private static CoreShell.Diagnostics From(DiagnosticsReported reported) => new(
        reported.Scope,
        reported.Microphone,
        reported.Suppression,
        reported.RedactionBacklog,
        reported.FramesDropped,
        reported.KeystrokesDropped,
        reported.EgressBlocked,
        reported.LocalOnly,
        reported.PolicyVersion,
        reported.CpuPercent,
        reported.WorkingSetBytes,
        reported.ServiceVersion);

    public DiagnosticsWindow(CoreShell.Diagnostics diagnostics)
        : this(CoreShell.DiagnosticsPanel.From(diagnostics))
    {
    }

    /// <summary>
    /// The panel as <see cref="CoreShell.DiagnosticsPanel"/> decided it, which is what says "unknown" on
    /// every row when the service is not answering. Every count used to be shown as zero and local-only
    /// as "no", from a literal in this file (2026-10-12 review, T-152).
    /// </summary>
    public DiagnosticsWindow(CoreShell.DiagnosticsPanel panel)
    {
        InitializeComponent();
        DataContext = new DiagnosticsViewModel(panel);
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
