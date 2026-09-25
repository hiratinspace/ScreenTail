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
    private const string NothingToReview = "No draft to review yet. Record a session and stop it with Ctrl+Alt+S.";
    private const string SettingsLater = "Settings arrive when the capture service answers.";

    private readonly ShellState _state;
    private readonly Func<string, CancellationToken, Task<object?>>? _loadReview;
    private readonly Func<object>? _history;
    private readonly Func<object>? _settings;
    private object? _historyPane;
    private object? _settingsPane;
    private string? _reviewShown;

    public ShellViewModel()
        : this(new ShellState())
    {
    }

    /// <param name="loadReview">
    /// Builds the Review pane for a session id, over the pipe — or returns null when the service would
    /// not hand it over. Injected because this view model must not know about the pipe.
    /// </param>
    /// <param name="history">Builds the History pane, once.</param>
    /// <param name="settings">Builds the Settings pane, once (ST-082).</param>
    public ShellViewModel(
        ShellState state,
        Func<string, CancellationToken, Task<object?>>? loadReview = null,
        Func<object>? history = null,
        Func<object>? settings = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _loadReview = loadReview;
        _history = history;
        _settings = settings;
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

    /// <summary>
    /// What the content area shows: a <see cref="Review.ReviewViewModel"/>, a
    /// <see cref="History.HistoryViewModel"/>, or a sentence saying why neither. The window picks a
    /// template by type.
    /// </summary>
    [ObservableProperty]
    public partial object? Content { get; set; } = NothingToReview;

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
        Status = Describe(snapshot);
        Show(snapshot);
    }

    private void Show(ShellSnapshot snapshot)
    {
        switch (snapshot.View)
        {
            case ShellView.Review when snapshot.Reviewing is null:
                _reviewShown = null;
                Content = NothingToReview;
                break;
            case ShellView.Review when snapshot.Reviewing != _reviewShown:
                _ = LoadReviewAsync(snapshot.Reviewing);
                break;
            case ShellView.History:
                _historyPane ??= _history?.Invoke();
                Content = _historyPane ?? "History arrives when the capture service answers.";
                if (_historyPane is History.HistoryViewModel list)
                {
                    _ = list.RefreshAsync();
                }

                break;
            case ShellView.Settings:
                _settingsPane ??= _settings?.Invoke();
                Content = _settingsPane ?? SettingsLater;
                if (_settingsPane is Settings.SettingsViewModel settings)
                {
                    _ = settings.Integrations.LoadAsync();
                }

                break;
        }
    }

    /// <summary>
    /// Asks for the session and shows the pane when it arrives. The id is remembered as shown before
    /// the await, so a second snapshot for the same session while it loads does not ask twice; a snapshot
    /// for a different session wins, because the last thing asked for is the thing to show.
    /// </summary>
    private async Task LoadReviewAsync(string sessionId)
    {
        _reviewShown = sessionId;
        Content = "Asking the capture service for the session…";
        var pane = _loadReview is null ? null : await _loadReview(sessionId, CancellationToken.None).ConfigureAwait(true);
        if (_reviewShown != sessionId)
        {
            return;
        }

        if (pane is null)
        {
            _reviewShown = null;
            Content = "The capture service could not hand over that session. Open it again from History.";
            return;
        }

        Content = pane;
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

        if (snapshot.KnownCapture is not { } capture)
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
