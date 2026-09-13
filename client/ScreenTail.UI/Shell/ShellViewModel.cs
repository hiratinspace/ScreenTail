using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The shell's window, bound to <see cref="ShellState"/> (ST-070).
///
/// It holds no state of its own beyond what WPF needs to bind to: every property here is derived from the
/// last snapshot the store handed out. That is the point of the store — Review, History and the tray all
/// read one thing, so a technician can never see "recording" in one window and "idle" in another.
///
/// Everything the view binds to is a string or a visibility, computed here rather than in a converter,
/// because the wording is Spec §5's and belongs somewhere it can be read and changed without opening XAML.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ShellState _state;

    public ShellViewModel()
        : this(new ShellState())
    {
    }

    public ShellViewModel(ShellState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _state.Changed += snapshot =>
        {
            // The store raises on whatever thread the IPC client is reading on; WPF bindings are the
            // dispatcher's. Marshalling here rather than at the store keeps the store free of WPF.
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                _ = dispatcher.BeginInvoke(() => Apply(snapshot));
                return;
            }

            Apply(snapshot);
        };

        Apply(_state.Snapshot);
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "Connecting to the capture service…";

    [ObservableProperty]
    public partial string Banner { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility BannerVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string CurrentView { get; set; } = nameof(ShellView.Review);

    [RelayCommand]
    private void Navigate(string? view)
    {
        if (Enum.TryParse<ShellView>(view, out var parsed))
        {
            _state.Navigate(parsed);
        }
    }

    /// <summary>
    /// Starting the service is ST-071's tray work; the banner offers it here because Spec §5 S1 puts the
    /// offer where the problem is reported. Wired to nothing yet rather than silently doing nothing
    /// forever — the command exists so the button is real when the tray can answer it.
    /// </summary>
    [RelayCommand]
    private static void StartService()
    {
    }

    private void Apply(ShellSnapshot snapshot)
    {
        Banner = snapshot.Banner ?? string.Empty;
        BannerVisibility = snapshot.Banner is null ? Visibility.Collapsed : Visibility.Visible;
        CurrentView = snapshot.View.ToString();
        Status = Describe(snapshot);
    }

    /// <summary>
    /// Spec §5 S1's tooltip wording: the state, the tool, and how long. A session that has been running
    /// for twelve minutes says so, because "recording" alone does not tell a technician whether the thing
    /// they started an hour ago is still going.
    /// </summary>
    private static string Describe(ShellSnapshot snapshot)
    {
        if (snapshot.Connection == ServiceConnection.Connecting)
        {
            return "Connecting to the capture service…";
        }

        if (snapshot.Capture is not { } capture)
        {
            return "Not connected to the capture service";
        }

        var state = capture.State switch
        {
            "recording" => "Recording",
            "paused" => "Paused",
            "suppressed" => "Paused — sensitive window",
            "finalizing" => "Finishing up",
            "draft_ready" => "Draft ready",
            "draft_failed" => "Draft failed",
            _ => "Idle",
        };

        var tool = string.IsNullOrEmpty(capture.RemoteTool) ? null : capture.RemoteTool;
        var elapsed = capture.ElapsedMs is { } ms
            ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", CultureInfo.InvariantCulture)
            : null;

        return (tool, elapsed) switch
        {
            (not null, not null) => $"{state} — {tool} ({elapsed})",
            (not null, null) => $"{state} — {tool}",
            (null, not null) => $"{state} ({elapsed})",
            _ => state,
        };
    }
}
