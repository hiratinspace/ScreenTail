using System.Runtime.Versioning;
using ScreenTail.Core.Settings;
using ScreenTail.Tests.Windows.Review;
using ScreenTail.UI.Settings.Integrations;

namespace ScreenTail.Tests.Windows.Settings;

/// <summary>
/// Settings → Integrations' Remove and the mapping table's Unmap, pressed twice while the first is on
/// its way (review of #274). The command's Execute does not look at whether it is running, so a second
/// invoke that does not go through the button sent the removal again and showed its refusal.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IntegrationsDoubleInvokeTests
{
    [Fact]
    public void WhileARemovalIsOnItsWayTheButtonIsOffAndASecondPressSendsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var gateway = new FakeIntegrations { Held = new TaskCompletionSource() };
            var screen = new IntegrationsViewModel(new IntegrationsPanel(gateway));

            var first = screen.Hudu.RemoveCommand.ExecuteAsync(null);

            Assert.True(screen.Hudu.RemoveCommand.IsRunning);
            Assert.False(screen.Hudu.RemoveCommand.CanExecute(null));
            screen.Hudu.RemoveCommand.Execute(null);
            Assert.False(screen.Hudu.RemoveCommand.CanExecute(null));
            Assert.Equal(["hudu"], gateway.Removed);

            gateway.Held.SetResult();
            await first;

            Assert.Equal(["hudu"], gateway.Removed);
            Assert.True(screen.Hudu.RemoveCommand.CanExecute(null));
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

    /// <summary>The service, remembering what it was asked to remove or unmap, and holding the answer until a test lets it go.</summary>
    private sealed class FakeIntegrations : IIntegrationsGateway
    {
        public required TaskCompletionSource Held { get; init; }

        public List<string> Removed { get; } = [];

        public List<string> Unmapped { get; } = [];

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IntegrationDetail>?>([]);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public async Task<string?> RemoveAsync(string provider, CancellationToken ct = default)
        {
            Removed.Add(provider);
            await Held.Task.ConfigureAwait(true);
            return null;
        }

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(new CheckOutcome(true, "Connected."));

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
