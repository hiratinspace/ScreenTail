using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Notifications;
using ScreenTail.Core.Review;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;
using ScreenTail.Platform.Ipc;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.History;
using ScreenTail.UI.Review;
using ScreenTail.UI.Settings;
using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Capture;
using ScreenTail.UI.Settings.Integrations;
using ScreenTail.UI.Settings.Privacy;
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
    private readonly ShellPreferencesStore _preferences;

    /// <summary>
    /// The thread that built the shell, which owns the tray icon and the pill. The application builds it
    /// on its UI thread, so this is that thread's dispatcher; taken here rather than from
    /// <see cref="Application.Current"/> so the shell needs a UI thread and nothing more, which is what
    /// lets its quit path be tested without a WPF application.
    /// </summary>
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private Onboarding.OnboardingWindow? _onboarding;

    /// <summary>
    /// The technician put the pill away, from its right-click or the tray. Held for this session only,
    /// never written to disk, and cleared when the next one starts (<see cref="_sessionUnderWay"/>).
    ///
    /// <see cref="ShellPreferencesStore"/> says why at length: persisting it would turn one right-click
    /// into a standing tray-only mode across reboots that nobody opted into, which is a recording with no
    /// pill and exactly what INV-4 forbids.
    /// </summary>
    private bool _hudHidden;

    /// <summary>
    /// The session the service last said was under way, so the shell can tell when a new one starts. Read
    /// and written on the UI thread only, in <see cref="ShowOrHideHud"/>.
    /// </summary>
    private string? _sessionUnderWay;

    /// <summary>
    /// Raises each notification once (ST-073). The service repeats its state on every reconnect, so
    /// without this a dropped pipe would announce a draft that has been ready for an hour.
    /// </summary>
    private readonly Notifier _notifier = new();

    /// <summary>Quit's wait for the Review panes of every shell window closed so far (T-169).</summary>
    private readonly ShellQuit _quit = new();
    private HudWindowHolder? _hud;
    private volatile bool _disposed;
    private ShellWindow? _window;

    public LiveShell()
        : this(OpenAsync, new ShellPreferencesStore(ShellPreferencesStore.DefaultPath))
    {
    }

    /// <param name="open">Opens one connection to the service. The verified pipe outside tests.</param>
    /// <param name="preferences">The shell's preferences file.</param>
    internal LiveShell(Func<string, CancellationToken, Task<ICaptureChannel>> open, ShellPreferencesStore preferences)
    {
        ArgumentNullException.ThrowIfNull(open);
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        _connection = new CaptureConnection(_state, ct => open(version, ct));

        _state.Changed += snapshot =>
        {
            // The pipe can deliver one more state while the connection is being disposed. The icon and
            // the pill are gone by then, and building the pill again for it would be a window nobody closes.
            if (_disposed)
            {
                return;
            }

            _tray.Update(snapshot);
            ShowOrHideHud(snapshot);
            Announce(snapshot);
        };

        _tray.Pause += () => Send(id => new PauseCommand { RequestId = id });
        _tray.Stop += () => Send(id => new StopCommand { RequestId = id });
        _tray.Discard += DiscardWithConfirmation;
        _tray.Open += ShowWindow;
        _tray.Setup += ShowOnboarding;
        _tray.ShowDiagnostics += ShowDiagnostics;
        _tray.HidePill += HidePillForThisSession;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
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

        // ST-083: the first start runs setup. Finishing it is remembered; the tray runs it again on request.
        if (_preferences.Load().OnboardedAt is null)
        {
            ShowOnboarding();
        }
    }

    private void ShowOnboarding()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(ShowOnboarding);
            return;
        }

        if (_onboarding is { IsLoaded: true })
        {
            _ = _onboarding.Activate();
            return;
        }

        _onboarding = Onboarding.OnboardingWindow.ForConnection(_connection, _preferences, () => { _state.Navigate(ShellView.Review); ShowWindow(); });
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
    }

    /// <summary>
    /// Puts the pill away while capture is known to be off, because the technician asked (INV-4). The tray
    /// icon stays: there is no state in which nothing indicates capture. <see cref="HudState"/> keeps the
    /// pill up whatever this says while a session runs, and the wish lasts until the next session starts.
    /// It used to last for the life of the process (2026-10-12 review, T-154).
    /// </summary>
    public void HidePillForThisSession()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(HidePillForThisSession);
            return;
        }

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
            return;
        }

        _ = AnnounceUpdateAsync(snapshot.Capture);
    }

    // ST-113 AC1: an update that is ready is announced once per version, and only when no session is
    // running (UpdateNotices holds that rule). The service is asked at most every ten minutes.
    private static readonly TimeSpan UpdateAskEvery = TimeSpan.FromMinutes(10);
    private DateTimeOffset _updateAsked = DateTimeOffset.MinValue;
    private string? _updateAnnounced;

    private async Task AnnounceUpdateAsync(CaptureStateSnapshot? capture)
    {
        if (UpdateNotices.For(capture, Probe) is null || DateTimeOffset.UtcNow - _updateAsked < UpdateAskEvery)
        {
            return;
        }

        _updateAsked = DateTimeOffset.UtcNow;
        var report = await new PipeUpdates(_connection).ReportAsync().ConfigureAwait(true);
        if (!_disposed && UpdateNotices.For(_state.Snapshot.Capture, report) is { } notice && notice.Key != _updateAnnounced)
        {
            _updateAnnounced = notice.Key;
            _tray.Notify(notice);
        }
    }

    /// <summary>A ready update, to ask <see cref="UpdateNotices"/> whether this state may hear about one at all.</summary>
    private static readonly UpdateReported Probe = new()
    {
        State = "ready",
        InstalledVersion = "0.0.0",
        AvailableVersion = "0.0.1",
        Channel = "stable",
        ChannelLocked = false,
        Sentence = string.Empty,
        CanInstall = true,
    };

    public void ShowWindow()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(ShowWindow);
            return;
        }

        if (_window is null)
        {
            var window = new ShellWindow(
                _state,
                LoadReviewAsync,
                () => new HistoryViewModel(_state, ListSessionsAsync),
                () => new SettingsViewModel(
                    new ActivationViewModel(new ActivationPanel(new PipeDevice(_connection), Environment.MachineName)),
                    new CaptureViewModel(new CapturePanel(new PipeCapture(_connection)), enabled => Task.FromResult(StartAtLogin.Apply(enabled))),
                    new PrivacyViewModel(new PrivacyPanel(new PipePrivacy(_connection)), ChooseAuditFile, EraseEverythingAsync),
                    new IntegrationsViewModel(new IntegrationsPanel(new PipeIntegrations(_connection))),
                    new Update.UpdatesViewModel(new UpdatesPanel(new PipeUpdates(_connection)))));

            // Once, when the window is made. A closed window's Review pane may still be writing its last,
            // and Quit waits for it even after the window has gone (T-169).
            window.Closed += (_, _) =>
            {
                _quit.WindowClosed(window.Released);
                _window = null;
            };
            _window = window;
        }

        _window.Show();
        _ = _window.Activate();
    }

    /// <summary>
    /// Takes the pill and the tray icon down, then the connection.
    ///
    /// The windows first, before anything is awaited, so they close on the calling thread: App.Dispose
    /// calls this on the UI thread and blocks on it. They used to close after the connection's dispose,
    /// whose ConfigureAwait(false) had moved the rest onto the thread pool, so closing the pill threw,
    /// the icon was never disposed (a ghost in the notification area) and every Quit ended in a crash
    /// report (2026-10-12 review, S-47, E-27, T-157). Awaiting back onto the UI thread instead would
    /// deadlock against that blocking call, so the order is the fix.
    ///
    /// The icon is disposed whatever the pill does, and so is the connection: a pill that throws as it
    /// closes still leaves no pipe open behind it, and the exception is raised once both are done.
    /// Closing the pill first stops the indicator reports, which is the safe direction for INV-4: the
    /// service suppresses capture when nothing indicates it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        try
        {
            try
            {
                _hud?.Close();
                _hud = null;
            }
            finally
            {
                _tray.Dispose();
            }
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The pill's window while the shell holds one, for the Windows tests.</summary>
    internal Hud.HudWindow? Pill => _hud?.Window;

    /// <summary>The tray icon, for the Windows tests.</summary>
    internal TrayIconHost Tray => _tray;

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
    /// The Review pane for one session, everything over the pipe (ST-085 remainder). Null when the
    /// service would not hand the session over, which the shell shows as a sentence rather than a crash.
    ///
    /// The note's save goes back the same way, and a save the service refused is thrown so the editor
    /// shows "Not saved — retrying" (Spec v0.4.3) instead of a tick over a note that never landed.
    /// </summary>
    private async Task<object?> LoadReviewAsync(string sessionId, CancellationToken ct)
    {
        var frames = new PipeReviewFrames(_connection);
        var session = await frames.LoadSessionAsync(sessionId, ct).ConfigureAwait(true);
        if (session is null)
        {
            return null;
        }

        // The publish pane's delegates, over the pipe. What the tenant has connected is asked now, once,
        // so the pane opens saying "Connect a PSA to publish" or ready, not guessing. The mapping prompt's
        // two calls (ST-097) ride the same connection.
        var publisher = new PipePublisher(_connection);
        var integrations = await publisher.IntegrationsAsync(ct).ConfigureAwait(true);
        var publish = new PublishPanel(
            session,
            integrations,
            publisher.SearchAsync,
            publisher.PublishAsync,
            mapping: new CompanyMapping(publisher.CompaniesAsync, publisher.MapAsync));

        // ST-077: the ticket number the coordinator read off the window when the session started. The
        // PSA is asked for it by id so the pane can show its summary and company beside the Suggested
        // badge; a number the PSA does not know is not suggested, and the picker is as before. Never for
        // a local-only session, whose banner says nothing from it leaves the device: opening Review is
        // not the technician publishing, and the number came off their screen (S-44, S-45). Nor when
        // local-only is in force now for a session recorded before it was, or when the service will not
        // say whether it is.
        if (await ReviewOpening.MayLookUpSuggestedTicketAsync(session.LocalOnly, session.SuggestedTicket, integrations.Count, LocalOnlyNowAsync, ct).ConfigureAwait(true)
            && session.SuggestedTicket is { } hinted)
        {
            var matches = await publisher.SearchAsync(hinted, ct).ConfigureAwait(true);
            if (matches.FirstOrDefault(m => m.Id == hinted) is { } match)
            {
                publish.Suggest(match);
            }
        }

        return new ReviewViewModel(
            session,
            frames,
            write: async (draft, c) =>
            {
                if (!await frames.SaveDraftAsync(sessionId, draft, c).ConfigureAwait(false))
                {
                    throw new IOException("The capture service did not save the note.");
                }
            },
            confirm: _ => ConfirmWindow.Asks(
                _window,
                "Discard this session?",
                "Everything captured in it goes: the screenshots, what was said, and the note. This cannot be undone.",
                TypedConfirmation.DiscardWord),
            publish: publish,
            timelineExpanded: _preferences.Load().TimelineExpanded,
            timelineToggled: expanded => _ = _preferences.Save(_preferences.Load() with { TimelineExpanded = expanded }));
    }

    /// <summary>Local-only as the service has it in force now (the admin's when locked), or null when it would not say.</summary>
    private async Task<bool?> LocalOnlyNowAsync(CancellationToken ct) =>
        (await _connection.RequestAsync<SettingsReported>(id => new GetSettingsCommand { RequestId = id }, ct).ConfigureAwait(true))?.EffectiveLocalOnly;

    private async Task<IReadOnlyList<SessionRow>?> ListSessionsAsync(CancellationToken ct) =>
        (await _connection.RequestAsync<SessionsListed>(id => new ListSessionsCommand { RequestId = id }, ct).ConfigureAwait(true))?.Sessions;

    /// <summary>
    /// Asks the service for a confirmation token, asks the technician to type the word, and sends both.
    ///
    /// Two round trips, and neither is decoration. The dialog is why a misclick no longer costs a
    /// technician the whole job — this item sits one below "Stop and draft" — and the token is why a
    /// process that is not the UI cannot skip the dialog: the service refuses a discard that does not
    /// carry one it issued moments ago (Spec §3, 2026-09-19 review).
    ///
    /// Nothing is sent if the service is not there, and the technician is told so. A discard sent into a
    /// dropped pipe would be reported as done and would not be.
    /// </summary>
    private void DiscardWithConfirmation()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(DiscardWithConfirmation);
            return;
        }

        _ = ConfirmAndSendAsync(
            "discard_session",
            "Discard this session?",
            "Everything captured in it goes: the screenshots, what was said, and the note. This cannot be undone.",
            token => new DiscardCommand { RequestId = 0, Confirmation = token });
    }

    /// <summary>Spec §5 S6 "Delete everything": the service issues the token, the technician types the phrase, and INV-12 does the rest.</summary>
    private Task EraseEverythingAsync() => ConfirmAndSendAsync(
        "erase_everything",
        "Delete everything on this device?",
        "Every session, draft, screenshot and transcript, your settings, and this device's tokens go. ScreenTail will need to be set up again. It cannot be undone.",
        token => new EraseAllLocalDataCommand { RequestId = 0, Confirmation = token });

    /// <summary>Where the technician wants the audit log. The service writes it and the panel copies it there.</summary>
    private string? ChooseAuditFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"screentail-audit-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON (*.json)|*.json",
            Title = "Export the audit log",
        };
        return dialog.ShowDialog(_window) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// The flow <see cref="ConfirmedAction"/> decides, with the dialog and the answer on the UI thread.
    ///
    /// The service's answer is shown when it is anything but done. It used to be dropped, so a refused
    /// "delete everything" (a session running, or the token gone stale while the phrase was typed) closed
    /// its dialog exactly as a successful one did, and the technician believed INV-12's erase had
    /// happened (2026-10-12 review, S-46, T-158). Not connected is said too: a click that did nothing and
    /// said nothing is the same mistake.
    /// </summary>
    private async Task ConfirmAndSendAsync(string action, string heading, string consequence, Func<string, IpcCommand> build)
    {
        var result = await ConfirmedAction.RunAsync(
            (asked, ct) => _connection.RequestAsync<ConfirmationIssued>(id => new RequestConfirmationCommand { RequestId = id, Action = asked }, ct),
            action,
            phrase => OnUiThread(() => ConfirmWindow.Asks(_window, heading, consequence, phrase)),
            token => id => build(token) with { RequestId = id },
            (command, ct) => _connection.SendAsync(command, ct)).ConfigureAwait(false);

        if (result.Outcome is ConfirmedOutcome.Refused or ConfirmedOutcome.NotAvailable)
        {
            _ = await OnUiThread(() => _window is { IsLoaded: true } owner
                ? MessageBox.Show(owner, result.Error, heading, MessageBoxButton.OK, MessageBoxImage.Warning)
                : MessageBox.Show(result.Error, heading, MessageBoxButton.OK, MessageBoxImage.Warning)).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="body"/> on the UI thread, from whichever thread a pipe answer arrived on.</summary>
    private Task<T> OnUiThread<T>(Func<T> body) =>
        !_dispatcher.CheckAccess()
            ? _dispatcher.InvokeAsync(body).Task
            : Task.FromResult(body());

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
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(QuitWithoutLeavingCaptureRunning);
            return;
        }

        // A finished session rests in draft_ready or draft_failed, which is nothing being captured (S-39).
        if (_state.Snapshot.KnownCapture is { } capture && !HudState.KnownIdle(capture))
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

        // The window closes and every closed window's Review pane is waited for, bounded, before the
        // application shuts down (ShellQuit, T-169). Waited for here, asynchronously on the UI thread,
        // because the application's own Dispose blocks that thread on DisposeAsync and a pane's writes
        // come back to it. The pill and the tray icon stay up while it waits, the safe side of INV-4. A
        // second Quit during the wait waits on the same task.
        _ = _quit.QuitAsync(() => _window?.Close(), () => Application.Current?.Shutdown());
    }

    private void ShowDiagnostics()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(ShowDiagnostics);
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
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => ShowOrHideHud(snapshot));
            return;
        }

        if (_disposed)
        {
            return;
        }

        if (_hud is null)
        {
            // The pill's own gestures reach the shell: double-click opens Review, right-click is the same
            // wish as the tray's item. Until they were wired only Mark moment did anything (T-372).
            _hud = new HudWindowHolder(_state, _connection, _preferences);
            _hud.ReviewRequested += OpenTheCurrentSession;
            _hud.HideRequested += HidePillForThisSession;
            _hud.SetOnline(NetworkInterface.GetIsNetworkAvailable());
        }

        // KnownCapture, not Capture: what the service last said is not what is true once it stops
        // answering, and this is the one decision that can take the indicator off the screen.
        var capture = snapshot.KnownCapture;
        if (HudState.SessionUnderWay(capture) is { } session && session != _sessionUnderWay)
        {
            // A session the shell has not seen running. Hiding was for the last one; and what Windows
            // allows is asked now, once, for the pill's no-mic segment, not on every state change.
            _sessionUnderWay = session;
            _hudHidden = false;
            _hud.SessionStarted();
            _ = ShowCapabilitiesAsync();
        }

        _hud.Apply(HudState.For(capture, hidden: _hudHidden));
    }

    /// <summary>The pill's double-click (Spec §5 S2): the shell, on Review of the session the service last named.</summary>
    private void OpenTheCurrentSession()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(OpenTheCurrentSession);
            return;
        }

        if (_state.Snapshot.KnownCapture?.SessionId is { Length: > 0 } session)
        {
            _state.OpenSession(session);
        }
        else
        {
            _state.Navigate(ShellView.Review);
        }

        ShowWindow();
    }

    /// <summary>
    /// Asks the service what Windows allows and hands it to the pill. A question the service cannot
    /// answer leaves the pill as it was: the segment is a nicety, and its absence claims nothing.
    /// </summary>
    private async Task ShowCapabilitiesAsync()
    {
        try
        {
            var reported = await _connection
                .RequestAsync<CapabilitiesReported>(id => new GetCapabilitiesCommand { RequestId = id })
                .ConfigureAwait(true);
            if (reported is not null && !_disposed)
            {
                _hud?.SetCapabilities(reported.FromWire());
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The pill's grey cloud (Spec §5 S2): whether this machine has a network at all. A local question,
    /// asked when Windows says the answer changed, never by polling and never by sending anything.
    /// </summary>
    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => OnNetworkChanged(sender, e));
            return;
        }

        if (!_disposed)
        {
            _hud?.SetOnline(e.IsAvailable);
        }
    }
}
