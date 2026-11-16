using ScreenTail.Core.Net;

namespace ScreenTail.Tests.Net;

/// <summary>
/// Where the capture service learns its backend (2026-11-15 review). A locked local-only is only as
/// strong as the place the address comes from: an installed build reads it from the machine-wide
/// setting an administrator writes and never from a per-user variable; a development build keeps the
/// variables the test loop, the demo and the end-to-end run are built on.
/// </summary>
public sealed class DeploymentAddressesTests
{
    private const string Machine = "https://screentail.msp.example/";
    private const string Elsewhere = "https://relay.attacker.example/";

    [Fact]
    public void AnInstalledBuildIgnoresTheEnvironmentAndUsesTheMachineSetting()
    {
        var address = DeploymentAddresses.Resolve(installed: true, Machine, Environment(Elsewhere, "a-token"));

        Assert.Equal(new Uri(Machine), address.Backend);
        Assert.Equal(AddressOrigin.Machine, address.Origin);
        Assert.True(address.EnvironmentIgnored);
    }

    [Fact]
    public void AnInstalledBuildWithNoMachineSettingHasNoBackendWhateverTheEnvironmentSays()
    {
        var address = DeploymentAddresses.Resolve(installed: true, machine: null, Environment(Elsewhere, null));

        Assert.Null(address.Backend);
        Assert.Equal(AddressOrigin.None, address.Origin);
        Assert.True(address.EnvironmentIgnored);
    }

    [Fact]
    public void AnInstalledBuildWithNothingSetSaysNothingWasIgnored()
    {
        var address = DeploymentAddresses.Resolve(installed: true, Machine, Environment(null, null));

        Assert.Equal(AddressOrigin.Machine, address.Origin);
        Assert.False(address.EnvironmentIgnored);
    }

    [Fact]
    public void ADevelopmentBuildUsesTheEnvironmentFirst()
    {
        // The demo's start.ps1 and the end-to-end run give the service SCREENTAIL_BACKEND for its own
        // process. That keeps working.
        var address = DeploymentAddresses.Resolve(installed: false, Machine, Environment("https://localhost:5001", null));

        Assert.Equal(new Uri("https://localhost:5001"), address.Backend);
        Assert.Equal(AddressOrigin.Environment, address.Origin);
        Assert.False(address.EnvironmentIgnored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void ADevelopmentBuildWithoutAUsableVariableFallsBackToTheMachineSetting(string? variable)
    {
        var address = DeploymentAddresses.Resolve(installed: false, Machine, Environment(variable, null));

        Assert.Equal(new Uri(Machine), address.Backend);
        Assert.Equal(AddressOrigin.Machine, address.Origin);
    }

    [Fact]
    public void NothingSetAnywhereIsNoBackendAndNotAnError()
    {
        var address = DeploymentAddresses.Resolve(installed: false, machine: "  ", Environment(null, null));

        Assert.Null(address.Backend);
        Assert.Equal(AddressOrigin.None, address.Origin);
    }

    [Fact]
    public void AnInstalledBuildTakesNoDeviceTokenFromItsEnvironment()
    {
        Assert.Null(DeploymentAddresses.FallbackToken(installed: true, Environment(Machine, "a-token")));
    }

    [Fact]
    public void ADevelopmentBuildStillTakesTheRunbooksToken()
    {
        var fallback = DeploymentAddresses.FallbackToken(installed: false, Environment(Machine, "a-token"));

        Assert.NotNull(fallback);
        Assert.Equal("a-token", fallback());
    }

    [Fact]
    public void AnInstalledBuildHasNoEnvironmentTokenEvenWithNoCredential()
    {
        // Through the session itself: not activated, and the variable set, is "not activated".
        using var http = new HttpClient();
        var device = new DeviceSession(
            http,
            new DeviceSessionTests.InMemoryCredentials(),
            DeploymentAddresses.FallbackToken(installed: true, Environment(Machine, "a-token")));

        Assert.Null(device.CurrentToken);
        Assert.StartsWith("Not activated", device.Standing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"/Program Files/ScreenTail/ScreenTail.Service.exe", true)]
    [InlineData(@"/program files/screentail/ScreenTail.Service.exe", true)]
    [InlineData(@"/Program Files (x86)/ScreenTail/ScreenTail.Service.exe", true)]
    [InlineData(@"/Users/tech/AppData/Local/ScreenTail.Dev/demo/app/ScreenTail.Service.exe", false)]
    [InlineData(@"/Program Files Extra/ScreenTail.Service.exe", false)]
    [InlineData(@"/Program Files/../Users/tech/ScreenTail.Service.exe", false)]
    [InlineData(null, false)]
    public void OnlyAnExecutableInsideProgramFilesCountsAsInstalled(string? executable, bool installed)
    {
        Assert.Equal(installed, DeploymentAddresses.UnderProgramFiles(executable, "/Program Files", "/Program Files (x86)", null));
    }

    private static Func<string, string?> Environment(string? backend, string? token) => name => name switch
    {
        DeploymentAddresses.BackendVariable => backend,
        DeploymentAddresses.DeviceTokenVariable => token,
        _ => null,
    };
}
