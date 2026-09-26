using ScreenTail.Core.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// The activation card (ST-010, Spec §5 S8 step 2, in Settings until onboarding exists): the code from
/// the invite goes in, the tenant's name comes back, and a refusal is the backend's own sentence.
/// </summary>
public sealed class ActivationPanelTests
{
    [Fact]
    public async Task LoadShowsTheDevicesStanding()
    {
        var gateway = new FakeDevice { Standing = new DeviceStanding(true, "Acme IT", 0, false, false, "Activated with Acme IT.") };
        var panel = new ActivationPanel(gateway, "TECH-LAPTOP");

        await panel.LoadAsync();

        Assert.True(panel.Activated);
        Assert.Equal("Acme IT", panel.TenantName);
        Assert.Equal("Activated with Acme IT.", panel.Standing);
    }

    [Theory]
    [InlineData("", "Enter the code from your invite.")]
    [InlineData("KX7P", "A code is ten letters and digits, like KX7PM-4R2WQ.")]
    [InlineData("KX7PM-4R2WQ", null)]
    [InlineData("kx7pm 4r2wq", null)]
    public async Task TheCodeIsCheckedBeforeAnythingIsSent(string code, string? problem)
    {
        var gateway = new FakeDevice();
        var panel = new ActivationPanel(gateway, "TECH-LAPTOP");
        await panel.LoadAsync();

        panel.Code = code;

        Assert.Equal(problem, panel.Problem);
        Assert.Equal(problem is null, panel.CanActivate);
    }

    [Fact]
    public async Task ActivatingSendsTheCodeAndTheMachineNameAndShowsTheTenant()
    {
        var gateway = new FakeDevice { TenantName = "Acme IT" };
        var panel = new ActivationPanel(gateway, "TECH-LAPTOP");
        await panel.LoadAsync();
        panel.Code = "kx7pm-4r2wq";

        Assert.True(await panel.ActivateAsync());

        Assert.Equal(("kx7pm-4r2wq", "TECH-LAPTOP"), gateway.Sent);
        Assert.True(panel.Activated);
        Assert.Equal("Acme IT", panel.TenantName);
        Assert.Equal(string.Empty, panel.Code);
        Assert.Null(panel.Problem);
    }

    [Fact]
    public async Task ARefusalIsTheBackendsSentenceAndTheCodeStays()
    {
        var gateway = new FakeDevice { Refusal = "Seat limit reached — contact your admin" };
        var panel = new ActivationPanel(gateway, "TECH-LAPTOP");
        await panel.LoadAsync();
        panel.Code = "KX7PM-4R2WQ";

        Assert.False(await panel.ActivateAsync());

        Assert.Equal("Seat limit reached — contact your admin", panel.Problem);
        Assert.False(panel.Activated);
        Assert.Equal("KX7PM-4R2WQ", panel.Code);
    }

    [Fact]
    public async Task WhenTheServiceDoesNotAnswerThePanelSaysSo()
    {
        var panel = new ActivationPanel(new FakeDevice { Standing = null }, "TECH-LAPTOP");

        await panel.LoadAsync();

        Assert.False(panel.Activated);
        Assert.Contains("capture service", panel.Standing, StringComparison.Ordinal);
    }

    private sealed class FakeDevice : IDeviceGateway
    {
        public DeviceStanding? Standing { get; set; } = new(false, null, 0, false, false, "Not activated. Enter the code from your invite.");

        public string? TenantName { get; set; }

        public string? Refusal { get; set; }

        public (string Code, string DeviceName)? Sent { get; private set; }

        public Task<DeviceStanding?> StandingAsync(CancellationToken ct = default) => Task.FromResult(Standing);

        public Task<ActivationAnswer> ActivateAsync(string code, string deviceName, CancellationToken ct = default)
        {
            Sent = (code, deviceName);
            if (Refusal is { } r)
            {
                return Task.FromResult(new ActivationAnswer(null, r));
            }

            Standing = new DeviceStanding(true, TenantName, 0, false, false, $"Activated with {TenantName}.");
            return Task.FromResult(new ActivationAnswer(TenantName, null));
        }
    }
}
