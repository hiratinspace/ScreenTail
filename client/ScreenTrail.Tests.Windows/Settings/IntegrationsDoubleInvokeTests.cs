using System.Runtime.Versioning;
using ScreenTrail.Core.Settings;
using ScreenTrail.Tests.Windows.Review;
using ScreenTrail.UI.Settings.Integrations;

namespace ScreenTrail.Tests.Windows.Settings;

/// <summary>
/// Settings → Integrations' Test connection and the mapping table's Unmap, pressed twice while the first
/// is on its way (review of #274). A second invoke that does not go through the button must not ask
/// again. Remove, which this once covered, is gone: the owner manages the connection in the web admin.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IntegrationsDoubleInvokeTests
{
    [Fact]
    public void WhileATestIsOnItsWayTheButtonIsOffAndASecondPressSendsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var gateway = new FakeIntegrations { Held = new TaskCompletionSource(), Rows = [new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••5678", DateTimeOffset.UnixEpoch, null, null)] };
            var screen = new IntegrationsViewModel(new IntegrationsPanel(gateway));
            await screen.LoadAsync();
            Assert.True(screen.Hudu.CheckCommand.CanExecute(null));

            var first = screen.Hudu.CheckCommand.ExecuteAsync(null);

            Assert.True(screen.Hudu.CheckCommand.IsRunning);
            Assert.False(screen.Hudu.CheckCommand.CanExecute(null));
            screen.Hudu.CheckCommand.Execute(null);
            Assert.Equal(["hudu"], gateway.Checked);

            gateway.Held.SetResult();
            await first;

            Assert.Equal(["hudu"], gateway.Checked);
            Assert.True(screen.Hudu.CheckCommand.CanExecute(null));
        });
    }

    [Fact]
    public void TestConnectionIsOffOnACardWithNothingConnected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var gateway = new FakeIntegrations { Held = new TaskCompletionSource() };
            var screen = new IntegrationsViewModel(new IntegrationsPanel(gateway));
            await screen.LoadAsync();

            Assert.False(screen.ConnectWise.CheckCommand.CanExecute(null));
            Assert.Equal(IntegrationCard.OwnerConnects, screen.ConnectWise.OwnerLine);
            Assert.Empty(gateway.Checked);
        });
    }

    [Fact]
    public void WhileAnUnmappingIsOnItsWayTheButtonIsOffAndASecondPressSendsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var gateway = new FakeIntegrations { Held = new TaskCompletionSource() };
            var screen = new IntegrationsViewModel(new IntegrationsPanel(gateway));
            var row = new MappingRow("Acme Dental", "Acme", "manual");

            var first = screen.UnmapCommand.ExecuteAsync(row);

            Assert.False(screen.UnmapCommand.CanExecute(row));
            screen.UnmapCommand.Execute(row);
            Assert.False(screen.UnmapCommand.CanExecute(row));
            Assert.Equal(["Acme Dental"], gateway.Unmapped);

            gateway.Held.SetResult();
            await first;

            Assert.Equal(["Acme Dental"], gateway.Unmapped);
            Assert.True(screen.UnmapCommand.CanExecute(row));
        });
    }

    /// <summary>The service, remembering what it was asked to test or unmap, and holding the answer until a test lets it go.</summary>
    private sealed class FakeIntegrations : IIntegrationsGateway
    {
        public required TaskCompletionSource Held { get; init; }

        public IReadOnlyList<IntegrationDetail> Rows { get; init; } = [];

        public List<string> Checked { get; } = [];

        public List<string> Unmapped { get; } = [];

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IntegrationDetail>?>(Rows);

        public async Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default)
        {
            Checked.Add(provider);
            await Held.Task.ConfigureAwait(true);
            return new CheckOutcome(true, "Connected.");
        }

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) =>
            Task.FromResult<CompanyMappingsPage?>(new CompanyMappingsPage([], []));

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public async Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default)
        {
            Unmapped.Add(psaCompany);
            await Held.Task.ConfigureAwait(true);
            return null;
        }
    }
}
