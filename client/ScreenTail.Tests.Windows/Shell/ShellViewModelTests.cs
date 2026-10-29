using System.Runtime.Versioning;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;
using ScreenTail.UI.History;
using ScreenTail.UI.Settings;
using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Capture;
using ScreenTail.UI.Settings.Integrations;
using ScreenTail.UI.Settings.Privacy;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The shell window's model carrying out what <see cref="ShellPanes"/> decides (ST-070; 2026-10-12 review
/// S-144, T-161, T-159, T-162). The decisions are held on any machine by ShellPanesTests; these hold the
/// model to them, with the real Settings panels and the real History list.
///
/// No dispatcher runs here, so the store's handler applies changes on the calling thread, as in
/// <see cref="ShellViewModelLifetimeTests"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellViewModelTests
{
    public ShellViewModelTests()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(System.Windows.Application.Current is null, "another test left a WPF Application behind; this model would queue every change on its dispatcher");
        }
    }

    [Fact]
    public void ACaptureStateChangeDoesNotReloadSettings()
    {
        // Every state change while Settings was open read all four panels again, which overwrote the
        // technician's unsaved edits; Save then stored the old values and said it had applied them.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var gateways = new CountingGateways();
        using var model = new ShellViewModel(state, settings: gateways.Settings);
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        state.Navigate(ShellView.Settings);
        var opened = gateways.Loads;
        Assert.True(opened > 0, "the Settings view loaded nothing when it opened");

        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 1_000 });
        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 2_000 });
        state.Lost();

        Assert.Equal(opened, gateways.Loads);
        Assert.IsType<SettingsViewModel>(model.Content);

        // The control: coming back to Settings reads it again.
        state.Navigate(ShellView.History);
        state.Navigate(ShellView.Settings);
        Assert.True(gateways.Loads > opened, "coming back to Settings did not read it again");
    }

    [Fact]
    public void TheServiceComingBackReadsSettingsAgainButNeverOverAnUnsavedEdit()
    {
        // Settings opened while the service was away said it did not answer, and went on saying so after
        // it came back. A reconnect reads the panels again, except one holding a change not yet saved.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var gateways = new CountingGateways
        {
            Privacy = new SettingsSnapshot(new ClientSettings(), LocalOnlyLocked: false, RetentionLocked: false, PolicyVersion: "default", EffectiveLocalOnly: false, EffectiveRetentionDays: 7),
        };
        using var model = new ShellViewModel(state, settings: gateways.Settings);
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        state.Navigate(ShellView.Settings);
        var settings = Assert.IsType<SettingsViewModel>(model.Content);
        Assert.Equal(1, gateways.LoadsOf("privacy"));
        Assert.Equal(1, gateways.LoadsOf("capture"));

        // The technician switches local-only on and has not pressed Save when the pipe drops and returns.
        settings.Privacy.LocalOnly = true;
        state.Lost();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });

        Assert.Equal(1, gateways.LoadsOf("privacy"));
        Assert.True(settings.Privacy.LocalOnly, "the reconnect overwrote the technician's unsaved local-only switch");
        Assert.Equal(2, gateways.LoadsOf("capture"));
        Assert.Equal(2, gateways.LoadsOf("activation"));
        Assert.Equal(2, gateways.LoadsOf("integrations"));

        // Put back as it was, nothing is unsaved, and the next reconnect reads it too.
        settings.Privacy.LocalOnly = false;
        state.Lost();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        Assert.Equal(2, gateways.LoadsOf("privacy"));
        Assert.Equal(3, gateways.LoadsOf("capture"));
    }

    [Fact]
    public void ReturningToReviewShowsTheSessionAgain()
    {
        // Review for s1, then History, then s1 opened again from History. Only a different session used
        // to count as a change, so the History pane stayed on screen under the Review heading.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var loads = new List<string>();
        using var model = new ShellViewModel(
            state,
            loadReview: (id, _) =>
            {
                loads.Add(id);
                return Task.FromResult<object?>("pane " + id);
            },
            history: () => new HistoryViewModel(state, _ => Task.FromResult<IReadOnlyList<SessionRow>?>([])));
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.DraftReady, SessionId = "s1", DraftsReady = 1 });
        Assert.Equal("pane s1", model.Content);

        state.Navigate(ShellView.History);
        Assert.IsType<HistoryViewModel>(model.Content);
        state.OpenSession("s1");

        Assert.Equal("pane s1", model.Content);
        Assert.Equal(["s1"], loads);
    }

    [Fact]
    public void APaneThatArrivesWhileAnotherViewIsOpenWaitsForReview()
    {
        // A slow load used to put the Review pane over whatever the technician had moved on to.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var pane = new TaskCompletionSource<object?>();
        using var model = new ShellViewModel(
            state,
            loadReview: (_, _) => pane.Task,
            history: () => new HistoryViewModel(state, _ => Task.FromResult<IReadOnlyList<SessionRow>?>([])));
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.DraftReady, SessionId = "s1", DraftsReady = 1 });
        state.Navigate(ShellView.History);

        pane.SetResult("pane s1");

        Assert.IsType<HistoryViewModel>(model.Content);
        state.Navigate(ShellView.Review);
        Assert.Equal("pane s1", model.Content);
    }

    [Theory]
    [InlineData("recording_with_audio")]
    [InlineData("IDLE")]
    public void TheShellNeverCallsAStateItDoesNotKnowIdle(string unknown)
    {
        // The tray and the pill say unknown. The shell said "Idle", so one window of three told the
        // technician nothing was happening when a newer service may have been recording (T-162).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        using var model = new ShellViewModel(state);

        state.Connected(new CaptureStateSnapshot { State = unknown });

        Assert.Equal(ShellStatus.Unknown, model.Status);
        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Idle });
        Assert.Equal("Idle", model.Status);
    }

    /// <summary>Every Settings panel's gateway, counting the reads that reach the service.</summary>
    private sealed class CountingGateways : IDeviceGateway, ICaptureGateway, IPrivacyGateway, IIntegrationsGateway
    {
        private readonly Dictionary<string, int> _loads = [];

        public int Loads { get; private set; }

        /// <summary>What the privacy gateway answers; null is a service that did not.</summary>
        public SettingsSnapshot? Privacy { get; init; }

        public int LoadsOf(string panel) => _loads.GetValueOrDefault(panel);

        public SettingsViewModel Settings() => new(
            new ActivationViewModel(new ActivationPanel(this, "TECH-LAPTOP")),
            new CaptureViewModel(new CapturePanel(this), _ => Task.CompletedTask),
            new PrivacyViewModel(new PrivacyPanel(this), () => null, () => Task.CompletedTask),
            new IntegrationsViewModel(new IntegrationsPanel(this)),
            new UI.Update.UpdatesViewModel(new UpdatesPanel(new NoUpdates())));

        public Task<DeviceStanding?> StandingAsync(CancellationToken ct = default) => Count<DeviceStanding>("activation");

        public Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        Task<CaptureSnapshot?> ICaptureGateway.LoadAsync(CancellationToken ct) => Count<CaptureSnapshot>("capture");

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        Task<SettingsSnapshot?> IPrivacyGateway.LoadAsync(CancellationToken ct) => Count("privacy", Privacy);

        public Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<AuditSaved> ExportAuditAsync(string destination, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Count<IReadOnlyList<IntegrationDetail>>("integrations");

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) => Count<CompanyMappingsPage>("mappings");

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        private Task<T?> Count<T>(string panel, T? answer = null)
            where T : class
        {
            Loads++;
            _loads[panel] = LoadsOf(panel) + 1;
            return Task.FromResult(answer);
        }
    }
}
