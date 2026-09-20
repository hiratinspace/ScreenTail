using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Windows;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Notifications;
using ScreenTail.Core.Shell;
using ScreenTail.Platform.Ipc;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Tray;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The running application, as opposed to the screenshot harness (ST-085).
///
/// This is the piece the UI process did not have. Every window was reachable, tested and rendered in CI,
/// and none of them was connected to anything: the shell injected a literal snapshot, the HUD was built
/// only by the CI harness, and no tray icon existed at all. INV-4 — capture is always visibly indicated —
/// was true of the design and of nothing that ran (weaknesses P0-2).
///
/// It owns four things and no state of its own:
/// <list type="bullet">
/// <item>the <see cref="ShellState"/> store every window binds to, so two windows can never disagree;</item>
/// <item>the <see cref="CaptureConnection"/> that keeps the pipe alive and the store fed;</item>
/// <item>the tray icon, which is the indicator that cannot be dismissed;</item>
/// <item>the HUD, which is shown whenever the state is anything but known-idle.</item>
/// </list>
///
/// The UI never opens the store and never references the service's project. Everything it knows arrives
/// over the pipe, which is what keeps INV-1's read-path filtering in one place instead of one per window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LiveShell : IAsyncDisposable
{
    private readonly ShellState _state = new();
    private readonly TrayIconHost _tray = new();
    private readonly CaptureConnection _connection;
    private readonly ShellPreferencesStore _preferences = new(ShellPreferencesStore.DefaultPath);

    /// <summary>
    /// The technician right-clicked the pill away. Held for this session only, never written to disk.
    ///
    /// <see cref="ShellPreferencesStore"/> says why at length: persisting it would turn one right-click
    /// into a standing tray-only mode across reboots that nobody opted into, which is a recording with no
    /// pill and exactly what INV-4 forbids.
    /// </summary>
    private bool _hudHidden;

    /// <summary>
    /// Raises each notification once (ST-073). The service repeats its state on every reconnect, so
    /// without this a dropped pipe would announce a draft that has been ready for an hour.
    /// </summary>
    private readonly Notifier _notifier = new();
    private HudWindowHolder? _hud;
    private ShellWindow? _window;

    public LiveShell()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        _connection = new CaptureConnection(_state, ct => OpenAsync(version, ct));

        _state.Changed += snapshot =>
        {
            _tray.Update(snapshot);
            ShowOrHideHud(snapshot);
            Announce(snapshot);
        };

        _tray.Pause += () => Send(id => new PauseCommand { RequestId = id });
        _tray.Stop += () => Send(id => new StopCommand { RequestId = id });
        _tray.Discard += DiscardWithConfirmation;
        _tray.Open += ShowWindow;
        _tray.ShowDiagnostics += ShowDiagnostics;
        _tray.HidePill += HidePillForThisSession;
        _tray.NotificationClicked += _ => ShowWindow();
        _tray.Quit += QuitWithoutLeavingCaptureRunning;
    }

    /// <summary>The store every window binds to.</summary>
    public ShellState State => _state;

    public CaptureConnection Connection => _connection;

    /// <summary>
    /// Shows the tray icon and starts connecting.
    ///
    /// The icon appears before the first connection attempt finishes, showing the grey "not connected"
    /// state. That is on purpose: an indicator that only appears once everything is working is absent
    /// precisely when a technician most needs to know something is wrong.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _tray.Show(_state.Snapshot);
        ShowOrHideHud(_state.Snapshot);
        await _connection.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The one way capture runs without a pill, and only ever because the technician asked in this
    /// session (INV-4). The tray icon stays: there is no state in which nothing indicates capture.
    /// </summary>
    public void HidePillForThisSession()
    {
        _hudHidden = true;
        ShowOrHideHud(_state.Snapshot);
    }

    /// <summary>
    /// Says whatever this state has to say, once.
    ///
    /// Spec §4 forbids interrupting a recording and <see cref="Notices"/> holds that rule, so this can be
    /// called on every change without checking: during a session it returns nothing.
    /// </summary>
    private void Announce(ShellSnapshot snapshot)
    {
        if (_notifier.Observe(snapshot.Capture) is { } notice)
        {
            _tray.Notify(notice);
        }
    }

    public void ShowWindow()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(ShowWindow);
            return;
        }

        _window ??= new ShellWindow(_state);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _ = _window.Activate();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _hud?.Close();
        _tray.Dispose();
    }

    /// <summary>
    /// Opens one connection to the service, verifying it before handing over the token (ST-012).
    ///
    /// The verifier is not optional here, and the parameter it is passed to refuses a default for the same
    /// reason: null means "give the session token to whoever answered", which is right for a test and
    /// wrong in a shipped application.
    /// </summary>
    private static async Task<ICaptureChannel> OpenAsync(string version, CancellationToken ct)
    {
        var token = IpcTokenFile.Read(IpcTokenFile.DefaultPath)
            ?? throw new TimeoutException("The capture service has not published a token yet.");
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return await IpcClient.ConnectAsync(
                IpcPipeNames.ForUser(sid),
                token,
                clientName: "ScreenTail.UI",
                serverVerifier: new WindowsServerVerifier(Environment.ProcessPath!),
                clientVersion: version,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(token);
        }
    }

    private void Send(Func<int, IpcCommand> build) =>
        _ = _connection.SendAsync(build, CancellationToken.None);

    /// <summary>
    /// Asks the service for a confirmation token, asks the technician to type the word, and sends both.
    ///
    /// Two round trips, and neither is decoration. The dialog is why a misclick no longer costs a
    /// technician the whole job — this item sits one below "Stop and draft" — and the token is why a
    /// process that is not the UI cannot skip the dialog: the service refuses a discard that does not
    /// carry one it issued moments ago (Spec §3, 2026-09-19 review).
    ///
    /// Nothing happens if the service is not there. A discard sent into a dropped pipe would be reported
    /// as done and would not be.
    /// </summary>
    private void DiscardWithConfirmation()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(DiscardWithConfirmation);
            return;
        }

        _ = ConfirmAndSendAsync(
            "discard_session",
            "Discard this session?",
            "Everything captured in it goes: the screenshots, what was said, and the note. This cannot be undone.",
            token => new DiscardCommand { RequestId = 0, Confirmation = token });
    }

    private async Task ConfirmAndSendAsync(string action, string heading, string consequence, Func<string, IpcCommand> build)
    {
        var issued = await _connection
            .RequestAsync<ConfirmationIssued>(id => new RequestConfirmationCommand { RequestId = id, Action = action })
            .ConfigureAwait(true);

        if (issued is null)
        {
            // Not connected, or the service does not know this action. Either way there is nothing to
            // confirm, and a dialog that led somewhere would be worse than none.
            return;
        }

        if (!ConfirmWindow.Asks(_window, heading, consequence, issued.Phrase))
        {
            // The token is left to expire. Telling the service it went unused would be another round
            // trip to say nothing, and thirty seconds from now it is worthless anyway.
            return;
        }

        Send(id => build(issued.Token) with { RequestId = id });
    }

    /// <summary>
    /// Quits, having made sure nothing is left recording without an indicator.
    ///
    /// Closing this process takes the tray icon and the pill with it, and the service goes on recording:
    /// it starts sessions by itself and owns the hotkeys. Quitting mid-session was therefore a way to
    /// arrive at invisible capture through a menu item (INV-4; 2026-09-19 review).
    ///
    /// The service suppresses capture within seconds of the last window closing, so this is belt and
    /// braces rather than the guarantee — but it is also the difference between a session the technician
    /// stopped and one that stops itself with a gap in the middle. Asked, not assumed: quitting during a
    /// live session is a choice, and Spec §3 says an interruption to a session is one to confirm.
    /// </summary>
    private void QuitWithoutLeavingCaptureRunning()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(QuitWithoutLeavingCaptureRunning);
            return;
        }

        if (_state.Snapshot.KnownCapture is { } capture && capture.State != CaptureStates.Idle)
        {
            var stop = MessageBox.Show(
                "A session is being captured. Quitting closes the recording indicator, so ScreenTail will "
                    + "stop capturing and finish the note."
                    + Environment.NewLine
                    + Environment.NewLine
                    + "Quit and finish the session?",
                "ScreenTail",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (stop != MessageBoxResult.OK)
            {
                return;
            }

            Send(id => new StopCommand { RequestId = id });
        }

        Application.Current?.Shutdown();
    }

    private void ShowDiagnostics()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(ShowDiagnostics);
            return;
        }

        _ = Diagnostics.DiagnosticsWindow.ShowLiveAsync(_connection);
    }

    /// <summary>
    /// Shows the pill whenever the state is anything but known-idle, and hides it only when the
    /// technician has asked and capture is known to be off.
    ///
    /// <see cref="HudState"/> already decides this; the window merely obeys. Deciding it again here would
    /// be the second place an auto-hide could creep in, and ST-048 has already had to close one.
    /// </summary>
    private void ShowOrHideHud(ShellSnapshot snapshot)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => ShowOrHideHud(snapshot));
            return;
        }

        _hud ??= new HudWindowHolder(_state, _connection, _preferences);
        // KnownCapture, not Capture: what the service last said is not what is true once it stops
        // answering, and this is the one decision that can take the indicator off the screen.
        _hud.SetVisible(HudState.For(snapshot.KnownCapture, hidden: _hudHidden).Visible);
    }
}
