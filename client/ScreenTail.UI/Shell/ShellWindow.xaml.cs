using System.IO;
using System.Windows;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.History;
using ScreenTail.UI.Review;
using ScreenTail.UI.Theme;
using AppTheme = ScreenTail.UI.Theme.AppTheme;

namespace ScreenTail.UI.Shell;

public partial class ShellWindow : Window
{
    private readonly string? _screenshotDirectory;

    public ShellWindow()
        : this((string?)null)
    {
    }

    /// <summary>
    /// The running application's shell, bound to the store the pipe feeds (ST-085).
    ///
    /// The other constructor builds a sample and renders it; this one shows what the service actually
    /// said. Keeping them apart is the point: a screenshot harness that shares a code path with the real
    /// window is one edit away from shipping the sample.
    /// </summary>
    public ShellWindow(
        ShellState state,
        Func<string, CancellationToken, Task<object?>> loadReview,
        Func<object> history)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loadReview);
        ArgumentNullException.ThrowIfNull(history);
        InitializeComponent();
        DataContext = new ShellViewModel(state, loadReview, history);
    }

    public ShellWindow(string? screenshotDirectory)
    {
        InitializeComponent();
        _screenshotDirectory = screenshotDirectory;

        // A state store with something in it, so a screenshot shows the shell doing its job rather than an
        // empty frame that would pass the check while proving nothing.
        var state = new ShellState();
        state.Connected(new Shared.Ipc.CaptureStateSnapshot
        {
            State = "recording",
            SessionId = "preview",
            RemoteTool = "screenconnect",
            ElapsedMs = 12 * 60 * 1000,
        });

        // The panes over the fixture, so the shell renders with something in its content area and a
        // binding that breaks in ReviewPane or HistoryPane fails this harness rather than the technician.
        var fixture = SessionFixture.Load();
        var frames = new FixtureFrames(SessionFixture.Directory());
        DataContext = new ShellViewModel(
            state,
            loadReview: (_, _) => Task.FromResult<object?>(new ReviewViewModel(fixture, frames)),
            history: () => new HistoryViewModel(state, _ => Task.FromResult<IReadOnlyList<SessionRow>?>(SampleRows())));
        if (_screenshotDirectory is not null)
        {
            ContentRendered += async (_, _) => await CaptureAsync(state);
        }
    }

    private static IReadOnlyList<SessionRow> SampleRows() =>
    [
        new() { Id = "s-1", StartedAt = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero), DurationMs = 61_000, Status = "draft", Tool = "screenconnect", Frames = 7, FramesPurged = 1 },
        new() { Id = "s-2", StartedAt = new DateTimeOffset(2026, 9, 15, 14, 20, 0, TimeSpan.Zero), DurationMs = 3_720_000, Status = "published", Tool = "rdp", Frames = 25, FramesPurged = 0 },
        new() { Id = "s-3", StartedAt = new DateTimeOffset(2026, 9, 15, 11, 5, 0, TimeSpan.Zero), DurationMs = 8_000, Status = "discarded", Tool = "screenconnect", Frames = 0, FramesPurged = 0 },
    ];

    private async Task CaptureAsync(ShellState state)
    {
        Directory.CreateDirectory(_screenshotDirectory!);

        // Both states worth looking at: working normally, and telling the technician the service is gone.
        foreach (var (name, arrange) in new (string, Action)[]
        {
            ("recording", () => { }),
            ("review", () => state.OpenSession("preview")),
            ("history", () => state.Navigate(ShellView.History)),
            ("service-down", () => { state.Navigate(ShellView.Review); state.Lost(); }),
        })
        {
            arrange();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
            {
                ThemeManager.Apply(theme, Application.Current.Resources);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                UpdateLayout();
                Save(Path.Combine(_screenshotDirectory!, $"shell-{name}-{theme.ToString().ToLowerInvariant()}.png"));
            }
        }

        // ST-071's panel renders in the same pass. It is a separate window, so it needs its own render
        // rather than appearing inside the shell's.
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
        {
            ThemeManager.Apply(theme, Application.Current.Resources);
            var panel = new Diagnostics.DiagnosticsWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
            panel.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            panel.UpdateLayout();
            Save(panel, Path.Combine(_screenshotDirectory!, $"panel-{theme.ToString().ToLowerInvariant()}.png"));
            panel.Close();
        }

        Application.Current.Shutdown();
    }

    private void Save(string path) => Render.WindowRenderer.Save(this, path);

    private static void Save(Window window, string path) => Render.WindowRenderer.Save(window, path);
}
