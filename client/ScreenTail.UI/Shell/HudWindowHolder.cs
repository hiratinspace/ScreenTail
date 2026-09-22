using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Hud;

namespace ScreenTail.UI.Shell;

/// <summary>
/// Keeps one pill alive for the life of the application and shows or hides it (ST-085).
///
/// A holder rather than creating a window each time, because the pill remembers where the technician
/// dragged it (Spec §5 S2) and a new window each session would put it back in the corner. Hiding is
/// <see cref="System.Windows.Window.Hide"/>, not close, for the same reason.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HudWindowHolder
{
    /// <summary>
    /// How often the service is told the pill is on screen.
    ///
    /// Comfortably inside <c>IndicatorReports.GoodFor</c>, so one missed message is not a suppressed
    /// session, and often enough that a UI which stops painting is noticed in seconds.
    /// </summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(2);

    private readonly HudWindow _window;
    private readonly DispatcherTimer _reporting;
    private bool _shown;

    public HudWindowHolder(ShellState state, CaptureConnection connection, ShellPreferencesStore preferences)
    {
        _window = new HudWindow(preferences);
        var model = new HudViewModel(state);
        model.MarkRequested += () => _ = connection.SendAsync(id => new MarkMomentCommand { RequestId = id });
        _window.DataContext = model;

        // INV-4. The service used to treat a pipe connection as proof that something on screen said
        // capture was happening, which a connection is not: this window could be on a monitor that had
        // since been unplugged, or frozen, and the service would never know (2026-09-20 review).
        //
        // On the dispatcher, deliberately. A report is a claim about what is painted, and only the
        // thread that paints it can say -- a timer on the thread pool would keep reporting cheerfully
        // while the UI thread was wedged, which is the case this exists to catch.
        _reporting = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ReportEvery };
        _reporting.Tick += (_, _) => ReportShowing(connection);
    }

    /// <summary>
    /// Tells the service the pill is on screen, when it is.
    ///
    /// Nothing is sent when it is not visible, when it has no size yet, or when it is somewhere no
    /// screen is -- the last being the case where a pill restored onto a monitor that has since been
    /// unplugged is drawn into empty coordinate space (ST-072).
    /// </summary>
    private void ReportShowing(CaptureConnection connection)
    {
        if (!_shown || _window.ActualWidth <= 0 || _window.ActualHeight <= 0)
        {
            return;
        }

        var virtualScreen = new ScreenArea(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight);

        if (!HudPlacement.IsOnScreen(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight, [virtualScreen]))
        {
            return;
        }

        _ = connection.SendAsync(id => new IndicatorShowingCommand
        {
            RequestId = id,
            X = _window.Left,
            Y = _window.Top,
            Width = _window.ActualWidth,
            Height = _window.ActualHeight,
        });
    }

    public void SetVisible(bool visible)
    {
        if (visible == _shown)
        {
            return;
        }

        _shown = visible;
        if (visible)
        {
            _window.Show();

            // Said at once rather than waiting for the first tick: a session starting must not spend two
            // seconds looking unindicated to the service.
            _reporting.Start();
        }
        else
        {
            _reporting.Stop();
            _window.Hide();
        }
    }

    public void Close()
    {
        _reporting.Stop();
        _window.Close();
    }
}
