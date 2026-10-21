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
/// A shell window's model lives as long as the window (ST-070, 2026-10-12 review E-23).
///
/// The tray builds a new window after each close, and every one of them used to stay subscribed to the
/// store. Each closed window went on asking the service for every new draft (and the backend for the
/// tenant's integrations, and the PSA for the suggested ticket), and went on reloading all four Settings
/// panels on every state change, for nobody, until the UI restarted. Disposing the model is what closing
/// the window does; these tests hold the model to it.
///
/// No dispatcher runs here, so the store's handler applies changes on the calling thread, which is what
/// lets these run without a window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellViewModelLifetimeTests
{
    [Fact]
    public void AClosedWindowStopsListening()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var loads = new List<string>();
        var model = new ShellViewModel(state, loadReview: (id, _) => { loads.Add(id); return Task.FromResult<object?>(null); });

        model.Dispose();
        model.Dispose();
        state.Connected(DraftReady("s1"));
        state.Observe(DraftReady("s2"));

        Assert.Empty(loads);
    }

    [Fact]
    public void AChangeAlreadyUnderWayWhenTheWindowClosesIsIgnored()
    {
        // The store copies its handler list before it raises. A listener ahead of the model closes the
        // window during that raise, and the copy still reaches the model once; it must do nothing.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var loads = 0;
        ShellViewModel? model = null;
        state.Changed += _ => model?.Dispose();
        model = new ShellViewModel(state, loadReview: (_, _) => { loads++; return Task.FromResult<object?>(null); });

        state.Connected(DraftReady("s1"));

        Assert.Equal(0, loads);
    }

    [Fact]
    public void AClosedWindowsModelIsNotKeptAliveByTheStore()
    {
        // The store outlives every window. Once closed, nothing it holds may reach the model.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var closed = OpenAndClose(state);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(closed.IsAlive, "the store still holds a closed window's model");
        GC.KeepAlive(state);
    }

    [Fact]
    public void AnOpenWindowLoadsEachNewDraftOnce()
    {
        // The other half of the one above: without it, a model that never loaded anything would pass.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var loads = new List<string>();
        using var model = new ShellViewModel(state, loadReview: (id, _) => { loads.Add(id); return Task.FromResult<object?>("pane " + id); });

        state.Connected(DraftReady("s1"));
        state.Observe(DraftReady("s1"));

        Assert.Equal(["s1"], loads);
        Assert.Equal("pane s1", model.Content);
    }

    [Fact]
    public void OpeningAndClosingTheWindowManyTimesLeavesOneListener()
    {
        // The cost grew with every open. After any number of them, a new draft is asked for once.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var loads = 0;
        Task<object?> Load(string id, CancellationToken ct)
        {
            loads++;
            return Task.FromResult<object?>(null);
        }

        for (var i = 0; i < 25; i++)
        {
            new ShellViewModel(state, Load).Dispose();
        }

        using var open = new ShellViewModel(state, Load);
        state.Connected(DraftReady("s1"));

        Assert.Equal(1, loads);
    }

    [Fact]
    public void ALoadStillRunningWhenTheWindowClosesIsCancelledAndNotShown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var asked = CancellationToken.None;
        var pane = new TaskCompletionSource<object?>();
        var model = new ShellViewModel(state, loadReview: (_, ct) => { asked = ct; return pane.Task; });
        state.Connected(DraftReady("s1"));
        var loading = model.Content;
        Assert.False(asked.IsCancellationRequested);

        model.Dispose();
        pane.SetResult("pane s1");

        Assert.True(asked.IsCancellationRequested, "closing the window did not cancel the load");
        Assert.Equal(loading, model.Content);
    }

    [Fact]
    public void ALoadThatGivesUpWhenTheWindowClosesEndsQuietly()
    {
        // A loader that honours the token throws on close. The model is not there to show anything, so
        // the cancellation is the end of it rather than an error.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var model = new ShellViewModel(state, loadReview: async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        });
        state.Connected(DraftReady("s1"));
        var loading = model.Content;

        model.Dispose();

        Assert.Equal(loading, model.Content);
    }

    [Fact]
    public void AClosedWindowOnSettingsReloadsNothing()
    {
        // The Settings view reloads its panels on every state change while it is open. After the close,
        // the service and the backend hear nothing more from this model.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var gateways = new CountingGateways();
        var model = new ShellViewModel(state, settings: gateways.Settings);
        state.Navigate(ShellView.Settings);
        Assert.True(gateways.Loads > 0, "the Settings view loaded nothing while open");
        Assert.All(gateways.Tokens, t => Assert.False(t.IsCancellationRequested));
        var before = gateways.Loads;

        model.Dispose();
        state.Observe(Recording());
        state.Lost();

        Assert.Equal(before, gateways.Loads);
        Assert.All(gateways.Tokens, t => Assert.True(t.IsCancellationRequested));
    }

    [Fact]
    public void AClosedWindowOnHistoryRefreshesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var tokens = new List<CancellationToken>();
        var model = new ShellViewModel(state, history: () => new HistoryViewModel(state, ct =>
        {
            tokens.Add(ct);
            return Task.FromResult<IReadOnlyList<SessionRow>?>([]);
        }));
        state.Navigate(ShellView.History);
        var before = tokens.Count;
        Assert.True(before > 0, "the History view did not refresh while open");

        model.Dispose();
        state.Observe(Recording());

        Assert.Equal(before, tokens.Count);
        Assert.All(tokens, t => Assert.True(t.IsCancellationRequested));
    }

    /// <summary>Not inlined, so no local in the test keeps the model reachable when the collector runs.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose(ShellState state)
    {
        var model = new ShellViewModel(state, loadReview: (_, _) => Task.FromResult<object?>(null));
        model.Dispose();
        return new WeakReference(model);
    }

    private static CaptureStateSnapshot DraftReady(string id) =>
        new() { State = CaptureStates.DraftReady, SessionId = id, DraftsReady = 1 };

    private static CaptureStateSnapshot Recording() =>
        new() { State = "recording", SessionId = "s9" };

    /// <summary>Every Settings panel's gateway, counting what reaches the service and keeping the token each was handed.</summary>
    private sealed class CountingGateways : IDeviceGateway, ICaptureGateway, IPrivacyGateway, IIntegrationsGateway
    {
        public int Loads => Tokens.Count;

        public List<CancellationToken> Tokens { get; } = [];

        public SettingsViewModel Settings() => new(
            new ActivationViewModel(new ActivationPanel(this, "TECH-LAPTOP")),
            new CaptureViewModel(new CapturePanel(this), _ => Task.CompletedTask),
            new PrivacyViewModel(new PrivacyPanel(this), _ => Task.CompletedTask, () => Task.CompletedTask),
            new IntegrationsViewModel(new IntegrationsPanel(this)));

        public Task<DeviceStanding?> StandingAsync(CancellationToken ct = default) => Count<DeviceStanding>(ct);

        public Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        Task<CaptureSnapshot?> ICaptureGateway.LoadAsync(CancellationToken ct) => Count<CaptureSnapshot>(ct);

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        Task<SettingsSnapshot?> IPrivacyGateway.LoadAsync(CancellationToken ct) => Count<SettingsSnapshot>(ct);

        public Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<string?> ExportAuditAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Count<IReadOnlyList<IntegrationDetail>>(ct);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) => Count<CompanyMappingsPage>(ct);

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");

        private Task<T?> Count<T>(CancellationToken ct)
            where T : class
        {
            Tokens.Add(ct);
            return Task.FromResult<T?>(null);
        }
    }
}
