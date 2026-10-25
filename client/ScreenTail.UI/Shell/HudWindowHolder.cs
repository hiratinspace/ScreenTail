using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Hud;
using Forms = System.Windows.Forms;

namespace ScreenTail.UI.Shell;

/// <summary>
/// Keeps one pill alive for the life of the application, shows or hides it, and tells the service when
/// an indicator is on screen (ST-085, INV-4).
///
/// A holder rather than creating a window each time, because the pill remembers where the technician
/// dragged it (Spec §5 S2) and a new window each session would put it back in the corner. Hiding is
/// <see cref="System.Windows.Window.Hide"/>, not close, for the same reason.
///
/// The pill's own gestures come out of here as events, so the shell can act on them: double-click opens
/// Review for the running session, right-click asks for the pill to be put away. Until they were wired,
/// both existed only in the render harness (2026-10-12 review, T-372).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HudWindowHolder
{
    /// <summary>
    /// How often the service is told an indicator is on screen.
    ///
    /// Comfortably inside <c>IndicatorReports.GoodFor</c>, so one missed message is not a suppressed
    /// session, and often enough that a UI which stops painting is noticed in seconds.
    /// </summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(2);

    private readonly HudWindow _window;
    private readonly HudViewModel _model;
    private readonly CaptureConnection _connection;
    private readonly DispatcherTimer _reporting;
    private HudSnapshot _hud = HudState.For(null);
    private bool _shown;

    public HudWindowHolder(ShellState state, CaptureConnection connection, ShellPreferencesStore preferences)
    {
        _connection = connection;
        _window = new HudWindow(preferences);
        _model = new HudViewModel(state);
        _model.MarkRequested += () => _ = connection.SendAsync(id => new MarkMomentCommand { RequestId = id });
        var gestures = new HudGestures(_model, () => _hud.Visible);
        gestures.ReviewRequested += () => ReviewRequested?.Invoke();
        gestures.HideRequested += () => HideRequested?.Invoke();
        _window.DataContext = _model;

        // The first report is not left to the timer. Show() lays the window out before it returns, but
        // the backstop costs nothing and a session starting must not spend two seconds looking
        // unindicated to the service (T-153).
        _window.Loaded += (_, _) => Report();

        // INV-4. The service used to treat a pipe connection as proof that something on screen said
        // capture was happening, which a connection is not: this window could be on a monitor that had
        // since been unplugged, or frozen, and the service would never know (2026-09-20 review).
        //
        // On the dispatcher, deliberately. A report is a claim about what is painted, and only the
        // thread that paints it can say -- a timer on the thread pool would keep reporting cheerfully
        // while the UI thread was wedged, which is the case this exists to catch.
        _reporting = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ReportEvery };
        _reporting.Tick += (_, _) => Report();
    }

    /// <summary>The technician double-clicked the pill: Review for the running session (Spec §5 S2).</summary>
    public event Action? ReviewRequested;

    /// <summary>
    /// The technician right-clicked the pill away. The shell owns the hidden flag, because the tray's menu
    /// item sets the same one; this only passes the wish on.
    /// </summary>
    public event Action? HideRequested;

    /// <summary>
    /// Shows or hides the pill as <see cref="HudState"/> decided, and keeps the service told.
    ///
    /// The reporting timer runs from the first call for the life of the holder, hidden or not. It used to
    /// stop with the pill, and the service asks whether anything indicates before it starts a session, so
    /// a pill put away between sessions refused every later start until the UI restarted (T-154).
    /// <see cref="HudState.Indicates"/> says what is reported while the pill is down.
    /// </summary>
    public void Apply(HudSnapshot hud)
    {
        ArgumentNullException.ThrowIfNull(hud);
        _hud = hud;

        if (hud.Visible && !_shown)
        {
            _shown = true;
            _window.Show();

            // Said at once rather than waiting for the first tick (T-153).
            Report();
        }
        else if (!hud.Visible && _shown)
        {
            _shown = false;
            _window.Hide();
        }

        if (!_reporting.IsEnabled)
        {
            _reporting.Start();
        }
    }

    /// <summary>A new session: the pill's own hidden flag is cleared, so it can be put away again later.</summary>
    public void SessionStarted() => _model.SessionStarted();

    /// <summary>What Windows allows, for the no-mic segment (ST-021, ST-072).</summary>
    public void SetCapabilities(CapabilityReport report) => _model.Capabilities = report;

    /// <summary>Whether a cloud draft could be asked for, for the offline segment (Spec §5 S2).</summary>
    public void SetOnline(bool online) => _model.Online = online;

    public void Close()
    {
        _reporting.Stop();
        _window.Close();
    }

    /// <summary>
    /// The monitors as rectangles in device pixels, for <see cref="HudPlacement.IsOnScreen"/>.
    ///
    /// The real monitors, not the virtual screen's bounding box. On an uneven layout the box covers
    /// dead space between and beside the monitors, and a pill restored into it reported itself as
    /// showing while nobody could see it (2026-10-12 review, S-42).
    /// </summary>
    internal static IReadOnlyList<ScreenArea> Screens() =>
        [.. Forms.Screen.AllScreens.Select(s => new ScreenArea(s.Bounds.Left, s.Bounds.Top, s.Bounds.Right, s.Bounds.Bottom))];

    private void Report()
    {
        if (!HudState.Indicates(_hud, PillOnScreen()))
        {
            return;
        }

        // The pill put away is the tray icon alone, and is said as an empty rectangle rather than as the
        // hidden window's old one: the rectangle is a claim about where the pill is, and it is nowhere.
        var pill = _hud.Visible;
        _ = _connection.SendAsync(id => new IndicatorShowingCommand
        {
            RequestId = id,
            X = pill ? _window.Left : 0,
            Y = pill ? _window.Top : 0,
            Width = pill ? _window.ActualWidth : 0,
            Height = pill ? _window.ActualHeight : 0,
        });
    }

    /// <summary>
    /// Whether the pill is up, has a size, and has its middle on some monitor.
    ///
    /// WPF measures in device-independent units and the monitors are in device pixels, so the pill's
    /// rectangle is taken through its own window's transform. That is exact on the monitor the pill is
    /// on and near enough on the way between two of different scale, which is far better than the
    /// bounding box this replaces.
    /// </summary>
    private bool PillOnScreen()
    {
        if (!_shown || !_window.IsVisible || _window.ActualWidth <= 0 || _window.ActualHeight <= 0)
        {
            return false;
        }

        var toDevice = PresentationSource.FromVisual(_window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var topLeft = toDevice.Transform(new Point(_window.Left, _window.Top));
        var size = toDevice.Transform(new Vector(_window.ActualWidth, _window.ActualHeight));
        return HudPlacement.IsOnScreen(topLeft.X, topLeft.Y, size.X, size.Y, Screens());
    }
}
