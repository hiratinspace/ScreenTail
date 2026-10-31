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
/// It lives as long as its window, not as long as the store. The tray opens a new window after each close,
/// so a model that kept listening to the store after its window closed would go on loading every new
/// draft and reloading Settings for nobody, once per window ever opened. <see cref="Dispose"/> stops it
/// listening and cancels what it was loading; <see cref="ShellWindow"/> calls it when it closes.
///
/// A Review pane it stops showing for good (another session's replaces it, its session goes away, or the
/// window closes) is disposed, which writes the last of the note and commits a pending delete (T-169).
/// <see cref="Released"/> finishes when all of that has, for Quit to wait on.
///
/// Everything the view binds to is a string or a visibility, computed here rather than in a converter,
/// because the wording is Spec §5's and belongs somewhere it can be read and changed without opening XAML.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private const string NothingToReview = "No draft to review yet. Record a session and stop it with Ctrl+Alt+S.";
    private const string SettingsLater = "Settings arrive when the capture service answers.";
    private const string Loading = "Asking the capture service for the session…";

    private readonly ShellState _state;
    private readonly Func<string, CancellationToken, Task<object?>>? _loadReview;
    private readonly Func<object>? _history;
    private readonly Func<object>? _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _closing;
    private volatile bool _disposed;
    private object? _historyPane;
    private readonly ShellPanes _panes = new();
    private object? _settingsPane;
    private readonly HeldPane _review = new();
    private int _loads;

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
        _closing = _lifetime.Token;
        _state.Changed += OnStateChanged;

        Apply(_state.Snapshot);
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "Connecting to the capture service…";

    [ObservableProperty]
    public partial string Banner { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility BannerVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// The view the store says is showing, so the header can mark its tab. Read off the snapshot like
    /// everything else here; the window does not keep its own idea of where it is.
    /// </summary>
    [ObservableProperty]
    public partial ShellView View { get; set; } = ShellView.Review;

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

    /// <summary>
    /// Finishes once every Review pane this model let go of has finished going: the last of the note
    /// written and a pending delete committed and answered (T-169). Complete until the window closes.
    /// </summary>
    public Task Released { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Stops listening to the store, cancels anything still loading, and lets go of the Review pane, whose
    /// last writes <see cref="Released"/> then waits for. Idempotent, and called on the dispatcher when the
    /// window closes. A change the store raised just before, and already queued for the dispatcher, finds
    /// the model disposed and does nothing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state.Changed -= OnStateChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Released = _review.ReleaseAsync();
    }

    private void OnStateChanged(ShellSnapshot snapshot)
    {
        // The store raises on whatever thread the IPC client is reading on; WPF bindings are the
        // dispatcher's. Marshalling here rather than at the store keeps the store free of WPF.
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Apply(snapshot));
            return;
        }

        Apply(snapshot);
    }

    private void Apply(ShellSnapshot snapshot)
    {
        // The store copies its handler list before it raises, so a raise already under way on the IPC
        // thread can still reach a model whose window has closed, directly or through the dispatcher.
        if (_disposed)
        {
            return;
        }

        Banner = snapshot.Banner ?? string.Empty;
        BannerVisibility = snapshot.Banner is null ? Visibility.Collapsed : Visibility.Visible;
        Status = ShellStatus.Describe(snapshot);
        View = snapshot.View;
        Show(snapshot);
    }

    /// <summary>
    /// Shows what <see cref="ShellPanes"/> decided. The decisions (when Settings is read, when Review is
    /// loaded or shown again) live there, where they are tested without WPF; this only does them.
    /// </summary>
    private void Show(ShellSnapshot snapshot)
    {
        var step = _panes.Next(snapshot);

        // The session under review was discarded (Spec §5 S3, S4). Its pane is let go of now, and a load
        // still under way for it shows nothing when it lands.
        if (step.ReleaseReview)
        {
            _loads++;
            _review.Hold(null);
        }

        switch (step.Action)
        {
            case PaneAction.NothingToReview:
                // A load still under way is for a session no longer to be shown, and the pane already
                // shown is finished with: off the screen first, then let go of, which writes its last.
                _loads++;
                Content = NothingToReview;
                _review.Hold(null);
                break;
            case PaneAction.LoadReview:
                _ = LoadReviewAsync(step.SessionId!);
                break;
            case PaneAction.ShowReview:
                // Back to the session it already had, from another view: its pane again, edits and all,
                // rather than the other view's pane left on screen (T-159). Still loading, it says so.
                Content = _review.Current ?? Loading;
                break;
            case PaneAction.History:
                _historyPane ??= _history?.Invoke();
                Content = _historyPane ?? "History arrives when the capture service answers.";
                if (_historyPane is History.HistoryViewModel list)
                {
                    _ = list.RefreshAsync(_closing);
                }

                break;
            case PaneAction.Settings:
                _settingsPane ??= _settings?.Invoke();
                Content = _settingsPane ?? SettingsLater;

                // Read when the view becomes Settings, and not again on each state change while it is
                // open: that overwrote unsaved edits, and Save then stored the old values (S-144, T-161).
                // Read again when the service comes back, but never a panel holding an unsaved change.
                if (step.LoadSettings && _settingsPane is Settings.SettingsViewModel settings)
                {
                    if (!step.KeepEdits || !settings.Activation.HasUnsavedEdits)
                    {
                        _ = settings.Activation.LoadAsync(_closing);
                    }

                    if (!step.KeepEdits || !settings.Capture.HasUnsavedEdits)
                    {
                        _ = settings.Capture.LoadAsync(_closing);
                    }

                    if (!step.KeepEdits || !settings.Privacy.HasUnsavedEdits)
                    {
                        _ = settings.Privacy.LoadAsync(_closing);
                    }

                    if (!step.KeepEdits || !settings.Integrations.HasUnsavedEdits)
                    {
                        _ = settings.Integrations.LoadAsync(_closing);
                    }

                    // Nothing on the Updates card is typed, so it is always read again.
                    _ = settings.Updates.LoadAsync(_closing);
                }

                break;
        }
    }

    /// <summary>
    /// Asks for the session and shows the pane when it arrives. A second snapshot for the same session
    /// while it loads does not ask twice (<see cref="ShellPanes"/> remembers it); a snapshot for a
    /// different session wins, because the last thing asked for is the thing to show. Closing the window
    /// cancels the load, and a pane that arrives after the close is not shown. A pane that arrives while
    /// the technician is looking at another view is kept for when they come back, not put over that view.
    /// The pane this one replaces is let go of as the load begins, and one that arrives to be shown nowhere
    /// is let go of as it arrives.
    /// </summary>
    private async Task LoadReviewAsync(string sessionId)
    {
        var load = ++_loads;
        Content = Loading;
        _review.Hold(null);

        // A load the close cancelled ends this task as cancelled, which nothing observes and nothing reports.
        var pane = _loadReview is null ? null : await _loadReview(sessionId, _closing).ConfigureAwait(true);

        // Only the latest load shows anything: one for another session, or for this one again once its
        // draft arrived, may have finished first.
        if (_disposed || load != _loads)
        {
            _review.LetGo(pane);
            return;
        }

        if (pane is null)
        {
            _panes.Failed(sessionId);
            if (_panes.View == ShellView.Review)
            {
                Content = "The capture service could not hand over that session. Open it again from History.";
            }

            return;
        }

        _review.Hold(pane);
        if (_panes.View == ShellView.Review)
        {
            Content = pane;
        }
    }
}
