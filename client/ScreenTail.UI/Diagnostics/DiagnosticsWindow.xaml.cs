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
    /// Opens the panel with what the service says, or with an honest blank when it cannot be asked
    /// (ST-085).
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

        var window = new DiagnosticsWindow(reported is null ? Unavailable : From(reported));
        window.Show();
        _ = window.Activate();
    }

    /// <summary>
    /// What the panel says when the service is not answering. Every count is left out rather than shown
    /// as zero: "0 frames dropped" and "we cannot ask" are different claims, and only one of them is true.
    /// </summary>
    private static CoreShell.Diagnostics Unavailable { get; } = new(
        Scope: "Not connected to the capture service — capture state unknown",
        Microphone: null,
        Suppression: null,
        RedactionBacklog: 0,
        FramesDropped: 0,
        KeystrokesDropped: 0,
        EgressBlocked: 0,
        LocalOnly: false,
        PolicyVersion: "unknown",
        CpuPercent: 0,
        WorkingSetBytes: 0,
        ClientVersion: "unknown");

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
