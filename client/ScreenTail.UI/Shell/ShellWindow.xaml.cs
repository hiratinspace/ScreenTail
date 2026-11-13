using System.IO;
using System.Windows;
using ScreenTail.Core.Onboarding;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.History;
using ScreenTail.UI.Review;
using ScreenTail.UI.Settings;
using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Capture;
using ScreenTail.UI.Settings.Integrations;
using ScreenTail.UI.Settings.Privacy;

namespace ScreenTail.UI.Shell;

#pragma warning disable CA1001 // A window ends in OnClosed, which disposes the model. WPF never calls Dispose on a window.
public partial class ShellWindow : Window
#pragma warning restore CA1001
{
    private readonly string? _screenshotDirectory;
    private readonly ShellViewModel _model;

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
        Func<object> history,
        Func<object> settings,
        RecordingClock? clock = null,
        Func<CancellationToken, Task<IReadOnlyList<SessionRow>?>>? sessions = null,
        Action? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loadReview);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        _model = new ShellViewModel(state, loadReview, history, settings, clock, sessions, diagnostics);
        DataContext = _model;
        BindFind();
    }

    /// <summary>
    /// <c>Ctrl+F</c>: focus the rail's search (Spec §5 S4), from anywhere in the window. The key is bound in
    /// the window's XAML, so the cross-platform tests read it off the file.
    /// </summary>
    public static System.Windows.Input.RoutedCommand FindCommand { get; } = new(nameof(FindCommand), typeof(ShellWindow));

    private void BindFind()
    {
        CommandBindings.Add(new System.Windows.Input.CommandBinding(FindCommand, (_, _) => Rail.FocusSearch()));
        BindRail();
    }

    /// <summary>
    /// Until when keyboard focus goes into the pane once it shows the session a rail row's Enter opened: a
    /// few seconds, so a pane that never arrives (the service could not hand it over) does not take focus
    /// later, when the technician has moved on.
    /// </summary>
    private DateTime _focusPaneUntil;

    /// <summary>
    /// Spec v0.6.9 S4: the rail is <c>layout.rail-width-narrow</c> in a window under
    /// <c>layout.rail-narrow-below</c> wide (<see cref="Core.History.RailLayout"/>), and <c>Enter</c> on a
    /// rail row opens its session and puts keyboard focus in the pane, where the review is done.
    /// </summary>
    private void BindRail()
    {
        // By the width the frame is laid out at, not the window's: the harness draws a window bigger than
        // the runner's screen by laying its content out outside it, and the rail should follow that.
        ShellFrame.SizeChanged += (_, e) => FitRail(e.NewSize.Width);
        Rail.RowActivated += (_, _) =>
        {
            _focusPaneUntil = DateTime.UtcNow.AddSeconds(5);
            FocusPaneSoon();
        };
        _model.PropertyChanged += (_, e) =>
        {
            if (DateTime.UtcNow < _focusPaneUntil && e.PropertyName == nameof(ShellViewModel.Content))
            {
                FocusPaneSoon();
            }
        };
    }

    /// <summary>The rail's width for a frame this wide, from the tokens.</summary>
    private void FitRail(double frameWidth)
    {
        var breakpoint = TryFindResource("Layout.rail-narrow-below") is double below ? below : 0;
        var wide = Core.History.RailLayout.IsWide(frameWidth, breakpoint);
        RailFrame.SetResourceReference(WidthProperty, wide ? "Layout.rail-width" : "Layout.rail-width-narrow");
    }

    /// <summary>Once the pane has drawn, focus its first control; a pane still loading waits for the next.</summary>
    private void FocusPaneSoon() =>
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (DateTime.UtcNow < _focusPaneUntil && FirstFocusable(Pane) is { } target)
                {
                    _focusPaneUntil = default;
                    _ = System.Windows.Input.Keyboard.Focus(target);
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>The first visible, enabled tab stop under <paramref name="parent"/>, in the visual tree's order.</summary>
    private static System.Windows.IInputElement? FirstFocusable(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }

            if (child is UIElement { IsEnabled: true, Focusable: true } element && System.Windows.Input.KeyboardNavigation.GetIsTabStop(element))
            {
                return element;
            }

            if (FirstFocusable(child) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary>
    /// The sample shell's store, so the accessibility suite (ST-084) can open the same session variants the
    /// screenshot harness does. Null on the running application's window, which has no sample.
    /// </summary>
    internal ShellState? Sample { get; }

    /// <summary>
    /// Spec §4: <c>?</c> opens the shortcut sheet, from any pane, unless the technician is typing into a
    /// text box, where it is a question mark (ST-084).
    /// </summary>
    protected override void OnPreviewTextInput(System.Windows.Input.TextCompositionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (ShortcutSheet.Opens(e.Text, System.Windows.Input.Keyboard.FocusedElement))
        {
            e.Handled = true;
            _ = ShortcutSheet.Open(this);
            return;
        }

        base.OnPreviewTextInput(e);
    }

    /// <summary>
    /// The tray builds a new window after each close, so this one's model must stop listening to the store
    /// now. Left subscribed, every window ever closed would keep loading each new draft and reloading
    /// Settings until the UI restarted.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _model.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Finishes once the Review pane the window held has written its last and committed a pending delete,
    /// after the window has closed (T-169). Quit waits on it before the application shuts down.
    /// </summary>
    internal Task Released => _model.Released;

    public ShellWindow(string? screenshotDirectory)
    {
        // The render collects binding errors from the first binding on, as the pill's and the note's do.
        // A pane bound to a property that is not there renders blank and still counts hundreds of
        // colours, so without this a renamed view-model property went green (2026-10-12 review, T-163).
        if (screenshotDirectory is not null)
        {
            Render.BindingErrors.Listen();
        }

        InitializeComponent();
        _screenshotDirectory = screenshotDirectory;
        if (screenshotDirectory is not null)
        {
            _ = Render.WindowRenderer.Intend(this);
        }

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
        _model = new ShellViewModel(
            state,
            loadReview: (id, _) => Task.FromResult<object?>(new ReviewViewModel(
                SampleSession(fixture, id),
                frames,
                write: SampleWrite(id),
                publish: SamplePublish(SampleSession(fixture, id), id),
                timelineExpanded: id == "preview-timeline",

                // The sample state is recording session "preview", so its Review shows Discard held with
                // the reason beside it, as the running application does.
                discardBlocked: () => state.Snapshot.KnownCapture is { State: Shared.Ipc.CaptureStates.Recording, SessionId: var live } && live == id ? SessionDiscard.StillCapturing : null,

                // The receipt's Next to check over the sample rows; opening one is the store's, as in the app.
                next: new Review.Publish.NextSessions(_ => Task.FromResult<IReadOnlyList<SessionRow>?>(SampleRows()), state.OpenSession),
                openSettings: () => state.Navigate(ShellView.Settings))),
            // Discard wired, so History's table shows Bulk discard held while the sample session records
            // (Spec §4): the reason is said beside the button and nothing is asked. Nothing is ever sent.
            history: () => new HistoryViewModel(
                state,
                // The rail's rows, so a render that empties them empties the table too.
                _ => Task.FromResult<IReadOnlyList<SessionRow>?>(_sampleRows),
                confirm: (_, _) => Task.FromResult(false),
                discard: (_, _, _) => Task.FromResult(new ConfirmedResult(ConfirmedOutcome.Declined)),
                discardBlocked: _ => state.Snapshot.KnownCapture is { State: Shared.Ipc.CaptureStates.Recording } ? SessionDiscard.WhileCapturing : null),
            settings: () => new SettingsViewModel(
                new ActivationViewModel(new ActivationPanel(new SampleDevice(), "TECH-LAPTOP")),
                new CaptureViewModel(new CapturePanel(new SampleCapture()), _ => Task.CompletedTask, new MicrophoneLevelWatch(new SampleCapture())),
                new PrivacyViewModel(new PrivacyPanel(new SamplePrivacy()), () => null, () => Task.FromResult(new ConfirmedResult(ConfirmedOutcome.Declined))),
                new IntegrationsViewModel(new IntegrationsPanel(new SampleIntegrations())),
                new Update.UpdatesViewModel(new UpdatesPanel(new SampleUpdates()))),
            sessions: _ => Task.FromResult<IReadOnlyList<SessionRow>?>(_sampleRows),
            diagnostics: () => { },
            time: new FixedClock(SampleNow));
        DataContext = _model;
        Sample = state;
        BindFind();
        if (_screenshotDirectory is not null)
        {
            // Once only. ContentRendered fires again whenever Content changes, and a render that is bigger
            // than the screen takes the content out of the window and puts it back, so a plain subscription
            // restarted the whole capture after every tall render and looped until the CI job timed out.
            EventHandler? capture = null;
            capture = async (_, _) =>
            {
                ContentRendered -= capture;
                await CaptureAsync(state);
            };
            ContentRendered += capture;
        }
    }

    /// <summary>
    /// The envelope and the receipt in each state Spec v0.6.0 §5 S3 draws, keyed by the session id the
    /// harness opens: a suggested ticket with a PSA connected, no ticket yet, no PSA at all, everything
    /// published, a time entry that failed with Retry offered, and an article waiting on the company
    /// mapping (ST-097). Fakes complete
    /// synchronously, so the state is there by the render.
    /// </summary>
    private static PublishPanel SamplePublish(Shared.Schema.Session fixture, string variant)
    {
        var ticket = new TicketMatch("48213", "Printer offline", "Acme Dental");
        // Both providers, so each link in the result list is named for the one it goes to.
        var connected = new[] { "connectwise", "hudu" };
        var article = new Uri("https://acme.huducloud.com/kba/printer-offline-spooler");
        IReadOnlyList<TicketMatch> Search(string _) => [ticket, new("48190", "VPN drops hourly", "Acme Dental"), new("48007", "New starter laptop", "Bright Smiles")];
        IReadOnlyList<DestinationResult> Publish(PublishRequest request, bool failTime) =>
            [.. request.Destinations.Select(d => d == Destination.TimeEntry && failTime
                ? new DestinationResult(d, false, null, "ConnectWise refused the member")
                : new DestinationResult(d, true, d == Destination.KbArticle ? article : new Uri("https://na.myconnectwise.net/v4_6_release/services/system_io/Service/fv_sr100_request.rails?service_recid=48213")))];

        // What the strip would send, as the running Review passes it: the frames the technician kept in.
        // An empty list made the receipt say 0 screenshots and 0 items masked for a session with three.
        IReadOnlyList<string> included = [.. new Core.Review.Filmstrip(fixture).ToPublish().Select(frame => frame.Id)];

        switch (variant)
        {
            case "preview-no-psa":
                return PublishPanel.Unconnected(fixture);

            // A PSA connected and no ticket bound: To reads "No ticket yet" and Publish waits with its reason.
            case "preview-no-ticket":
                return new PublishPanel(fixture, connected, (q, _) => Task.FromResult(Search(q)), (r, _) => Task.FromResult(Publish(r, failTime: false)));
            case "preview-published":
                {
                    var panel = new PublishPanel(fixture, connected, (q, _) => Task.FromResult(Search(q)), (r, _) => Task.FromResult(Publish(r, failTime: false)), time: new SampleClock());
                    panel.Suggest(ticket);
                    panel.KbArticle = true;
                    panel.PublishAsync(fixture.Draft!, included).GetAwaiter().GetResult();
                    return panel;
                }

            case "preview-partial":
                {
                    var panel = new PublishPanel(fixture, connected, (q, _) => Task.FromResult(Search(q)), (r, _) => Task.FromResult(Publish(r, failTime: true)), time: new SampleClock());
                    panel.Suggest(ticket);
                    panel.PublishAsync(fixture.Draft!, included).GetAwaiter().GetResult();
                    return panel;
                }

            case "preview-needs-mapping":
                {
                    IReadOnlyList<DestinationResult> Unmapped(PublishRequest request) =>
                        [.. request.Destinations.Select(d => d == Destination.KbArticle
                            ? new DestinationResult(d, false, null, "\"Acme Dental\" is not mapped to a company in the documentation platform. Choose one below, or map it in Settings → Integrations.", DestinationResult.NeedsMapping)
                            : new DestinationResult(d, true, new Uri("https://na.myconnectwise.net/v4_6_release/services/system_io/Service/fv_sr100_request.rails?service_recid=48213")))];
                    IReadOnlyList<CompanyChoice> Companies() => [new("7", "Acme Dental Group"), new("9", "Bright Smiles"), new("12", "Northwind Clinic")];
                    var panel = new PublishPanel(
                        fixture,
                        connected,
                        (q, _) => Task.FromResult(Search(q)),
                        (r, _) => Task.FromResult(Unmapped(r)),
                        mapping: new CompanyMapping(_ => Task.FromResult(Companies()), (_, _, _) => Task.FromResult(true)),
                        time: new SampleClock());
                    panel.Suggest(ticket);
                    panel.KbArticle = true;
                    panel.PublishAsync(fixture.Draft!, included).GetAwaiter().GetResult();
                    return panel;
                }

            default:
                {
                    var panel = new PublishPanel(fixture, connected, (q, _) => Task.FromResult(Search(q)), (r, _) => Task.FromResult(Publish(r, failTime: false)));
                    panel.Suggest(ticket);
                    return panel;
                }
        }
    }

    /// <summary>
    /// The sample receipts' clock: Review opened at 14:20 and published 4 min 12 s later, the first
    /// reading being the panel's "opened" and every later one the publish. The system clock would read
    /// the same instant twice, since the sample publishes as the panel is made.
    /// </summary>
    private sealed class SampleClock : TimeProvider
    {
        private static readonly DateTimeOffset Opened = new(2026, 9, 15, 14, 20, 0, TimeSpan.Zero);
        private bool _read;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _read ? Opened + new TimeSpan(0, 4, 12) : Opened;
            _read = true;
            return now;
        }
    }

    /// <summary>
    /// Settings → Integrations as Spec §5 S7 draws it: ConnectWise connected, Hudu connected with a
    /// failed last check, and a mapping table with one of each confidence. Nothing here reaches a
    /// backend; the running application uses <see cref="PipeIntegrations"/>.
    /// </summary>
    /// <summary>An activated device, for the harness: the card as most technicians will see it.</summary>
    private sealed class SampleDevice : IDeviceGateway
    {
        public Task<DeviceStanding?> StandingAsync(CancellationToken ct = default) =>
            Task.FromResult<DeviceStanding?>(new DeviceStanding(true, "Contoso MSP", 0, false, false, "Activated with Contoso MSP."));

        public Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default) =>
            Task.FromResult(new ActivationAnswer("Contoso MSP", null));
    }

    /// <summary>An update ready to install on a channel the admin locked, so the harness renders Install and the lock (ST-113).</summary>
    private sealed class SampleUpdates : IUpdateGateway
    {
        public Task<UpdateSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<UpdateSnapshot?>(new UpdateSnapshot("ready", "0.1.240", "0.1.250", Core.Update.UpdateChannel.Pilot, ChannelLocked: true, "ScreenTail 0.1.250 is ready to install.", CanInstall: true));

        public Task<string?> SetChannelAsync(Core.Update.UpdateChannel channel, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> InstallAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    /// <summary>Privacy settings under an admin policy that locked local-only and retention, so the harness renders the locked variant (Spec §3).</summary>
    private sealed class SamplePrivacy : IPrivacyGateway
    {
        public Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<SettingsSnapshot?>(new SettingsSnapshot(
                new Shared.Settings.ClientSettings { LocalOnly = true, RetentionDays = 3, CustomPatterns = [@"ACME-\d{6}"], ExcludedProcesses = ["keepass"] },
                LocalOnlyLocked: true,
                RetentionLocked: true,
                PolicyVersion: "p20260925120000000",
                EffectiveLocalOnly: true,
                EffectiveRetentionDays: 3));

        public Task<string?> SaveAsync(Shared.Settings.ClientSettings settings, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<AuditSaved> ExportAuditAsync(string destination, CancellationToken ct = default) =>
            Task.FromResult(new AuditSaved(true, true, "All 0 audit rows verify."));
    }

    /// <summary>
    /// Capture settings with one tool off and a clashing hotkey, so the harness renders the inline conflict
    /// (ST-029); and a chosen headset that is not connected, so it renders the microphone's fallback note
    /// (ST-080 AC3). The level is a fixed number: the harness opens no device.
    /// </summary>
    private sealed class SampleCapture : ICaptureGateway, IMicrophoneLevelGateway
    {
        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<CaptureSnapshot?>(new CaptureSnapshot(
                new Shared.Settings.CaptureSettings { DisabledTools = ["anydesk"], GraceSeconds = 90, MicrophoneId = "{0.0.1.00000000}.{jabra}" },
                [new("screenconnect", "ScreenConnect"), new("rdp", "Remote Desktop"), new("anydesk", "AnyDesk")],
                [new Core.Speech.MicrophoneDevice("{0.0.1.00000000}.{array}", "Laptop microphone array", IsDefault: true)],
                PolicyForcesAllWindows: false,
                DefaultGraceSeconds: 120));

        public Task<string?> SaveAsync(Shared.Settings.CaptureSettings settings, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Core.Speech.MeterReading?> ReadLevelAsync(string? deviceId, CancellationToken ct = default) =>
            Task.FromResult<Core.Speech.MeterReading?>(new Core.Speech.MeterReading(MicrophoneLevelStates.Listening, 0.45, "{0.0.1.00000000}.{array}"));

        public Task StopLevelAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A blocked microphone with its deep link, so the harness renders the Permissions step's fix (AC3).</summary>
    private sealed class SampleCapabilities : ICapabilitiesGateway
    {
        public Task<IReadOnlyList<CapabilityStatus>?> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CapabilityStatus>?>(
            [
                new CapabilityStatus { Capability = "microphone", State = "blocked", Message = "Microphone access is off for desktop apps.", FixHint = "Allow desktop apps to use your microphone.", FixLink = "ms-settings:privacy-microphone" },
                new CapabilityStatus { Capability = "screen_capture", State = "ok", Message = "Screen capture works." },
                new CapabilityStatus { Capability = "input_hooks", State = "ok", Message = "Input hooks install." },
            ]);
    }

    private sealed class SampleTestSession : ITestSession
    {
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> StopAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class SampleIntegrations : IIntegrationsGateway
    {
        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IntegrationDetail>?>(
            [
                new("connectwise", "https://na.myconnectwise.net", "••••7Qa2", new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 25, 8, 30, 0, TimeSpan.Zero), null),
                new("hudu", "https://docs.contosomsp.com", "••••c09d", new DateTimeOffset(2026, 9, 20, 9, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 25, 8, 31, 0, TimeSpan.Zero), "Hudu rejected the API key. Update it in Settings → Integrations."),
            ]);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(new CheckOutcome(true, $"Connected to {(provider == "hudu" ? "https://docs.contosomsp.com" : "https://na.myconnectwise.net")}."));

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) =>
            Task.FromResult<CompanyMappingsPage?>(new CompanyMappingsPage(
                [new("7", "Acme Dental"), new("9", "Birch Legal"), new("12", "Pine Vet")],
                [
                    new("Acme Dental", "7", "Acme Dental", "exact"),
                    new("Birch Legal LLP", "9", "Birch Legal", "likely"),
                    new("Pine Veterinary Clinic", "12", "Pine Vet", "manual"),
                ]));

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    /// <summary>The sample rail's "now": the morning after the sample's Review, so each of its groups has rows.</summary>
    private static readonly DateTimeOffset SampleNow = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    /// <summary>What the sample rail lists. The harness empties it for the rail's empty render.</summary>
    private IReadOnlyList<SessionRow>? _sampleRows = SampleRows();

    /// <summary>A clock that always says <see cref="SampleNow"/>, so the rail's ages render the same on every run.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static IReadOnlyList<SessionRow> SampleRows() =>
    [
        new() { Id = "s-1", StartedAt = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero), DurationMs = 61_000, Status = "draft", Tool = "screenconnect", Frames = 7, FramesPurged = 1, Title = "Printer offline after the driver update", Ticket = "48213" },
        new() { Id = "s-2", StartedAt = new DateTimeOffset(2026, 9, 15, 14, 20, 0, TimeSpan.Zero), DurationMs = 3_720_000, Status = "published", Tool = "rdp", Frames = 25, FramesPurged = 0, Title = "Outlook keeps asking for the password", Ticket = "50117", Company = "Acme Dental" },
        new() { Id = "s-3", StartedAt = new DateTimeOffset(2026, 9, 15, 11, 5, 0, TimeSpan.Zero), DurationMs = 8_000, Status = "discarded", Tool = "screenconnect", Frames = 0, FramesPurged = 0 },

        // A publish that may have reached the ticket, so the screenshots and the accessibility scan see the second badge.
        new() { Id = "s-4", StartedAt = new DateTimeOffset(2026, 9, 14, 16, 40, 0, TimeSpan.Zero), DurationMs = 540_000, Status = "draft", Tool = "screenconnect", Frames = 12, FramesPurged = 0, PublishUnconfirmed = true, Title = "VPN drops every hour", Ticket = "50342", Company = "Northwind Traders" },

        // The rail's other groups: a partial capture to check, and one published the morning of SampleNow.
        // The partial one has nothing but its tool, as a row from a v10 service or a session not yet drafted
        // reads; the published one has a company too long for the rail, which ends in an ellipsis.
        new() { Id = "s-5", StartedAt = new DateTimeOffset(2026, 9, 16, 9, 20, 0, TimeSpan.Zero), DurationMs = 1_260_000, Status = "partial", Tool = "rdp", Frames = 9, FramesPurged = 0 },
        new() { Id = "s-6", StartedAt = new DateTimeOffset(2026, 9, 16, 8, 5, 0, TimeSpan.Zero), DurationMs = 720_000, Status = "published", Tool = "splashtop", Frames = 6, FramesPurged = 0, Title = "New starter laptop set up", Ticket = "50388", Company = "Contoso Community Pharmacy Group" },
    ];

    private async Task CaptureAsync(ShellState state)
    {
        Directory.CreateDirectory(_screenshotDirectory!);
        RestTheMouse();

        // Both states worth looking at: working normally, and telling the technician the service is gone.
        foreach (var (name, arrange) in new (string, Action)[]
        {
            ("recording", () => { }),
            ("review", () => state.OpenSession("preview")),
            ("review-no-psa", () => state.OpenSession("preview-no-psa")),
            ("review-no-ticket", () => state.OpenSession("preview-no-ticket")),
            ("review-published", () => state.OpenSession("preview-published")),
            ("review-partial", () => state.OpenSession("preview-partial")),
            ("review-needs-mapping", () => state.OpenSession("preview-needs-mapping")),
            ("review-timeline", () => state.OpenSession("preview-timeline")),
            ("history", () => state.Navigate(ShellView.History)),
            ("settings", () => state.Navigate(ShellView.Settings)),
            ("service-down", () => { state.Navigate(ShellView.Review); state.Lost(); }),
        })
        {
            arrange();
            await ThumbnailsAsync();
            foreach (var theme in Render.RenderThemes.All)
            {
                Render.RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                UpdateLayout();
                Save(Path.Combine(_screenshotDirectory!, Render.RenderThemes.FileName($"shell-{name}", theme)));
            }
        }

        // The service comes back for the passes below, which draw a working shell.
        state.Connected(new Shared.Ipc.CaptureStateSnapshot { State = "recording", SessionId = "preview", RemoteTool = "screenconnect", ElapsedMs = 12 * 60 * 1000 });
        await EnlargedAsync(state);
        await NoteAsync(state);
        await SettingsSectionsAsync(state);
        await FocusAsync(state);
        await RailAsync(state);
        await SmallWindowAsync(state);
        await HighDpiAsync(state);
        await OnboardingAsync();

        // The windows of their own: the one dialog's questions, the shortcut sheet and ST-071's panel,
        // answered and with the service not answering, which says unknown on every row rather than zeros
        // (T-152). Each is built, not only drawn: the tray's dialog carried a TextBlock style on its TextBox
        // from #86 to #228, which WPF refuses as the window is built, and nothing built it to find out (#281).
        await DialogsAsync(
            ("panel", () => new Diagnostics.DiagnosticsWindow()),
            ("panel-unanswered", () => new Diagnostics.DiagnosticsWindow(DiagnosticsPanel.Unanswered)));

        // Read by CI, which fails the step on anything but zero.
        var errors = Render.BindingErrors.Collected;
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        Console.WriteLine($"{errors.Count} binding errors");
        Render.RenderIndex.Write(_screenshotDirectory!);
        Application.Current.Shutdown();
    }

    /// <summary>
    /// ST-083's wizard at one step, over the sample gateways: what the screenshot harness renders, and what
    /// the accessibility suite opens (ST-084). Nothing here reaches a service.
    /// </summary>
    internal static async Task<Onboarding.OnboardingViewModel> SampleOnboardingAsync(OnboardingStep step)
    {
        var wizard = new OnboardingWizard(
            new Core.Settings.ActivationPanel(new SampleDevice(), "TECH-LAPTOP"),
            new SampleCapabilities(),
            new Core.Settings.CapturePanel(new SampleCapture()),
            new Core.Settings.IntegrationsPanel(new SampleIntegrations()),
            new SampleTestSession(),
            _ => { });
        wizard.Go(step);
        var model = new Onboarding.OnboardingViewModel(wizard, () => { });
        await model.LoadAsync().ConfigureAwait(true);
        return model;
    }

    private void Save(string path) => Render.WindowRenderer.Save(this, path);

    private static void Save(Window window, string path) => Render.WindowRenderer.Save(window, path);
}
