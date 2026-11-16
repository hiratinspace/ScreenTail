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
    public void AnInstalledBuildWithNoMachineSettingSaysAnAdministratorSetsIt()
    {
        var quiet = DeploymentAddresses.Resolve(installed: true, machine: null, Environment(null, null));
        var ignored = DeploymentAddresses.Resolve(installed: true, machine: null, Environment(Elsewhere, null));

        Assert.Equal(DeploymentAddresses.NoMachineAddress, quiet.AdministratorNote);
        Assert.Equal(DeploymentAddresses.NoMachineAddress + DeploymentAddresses.EnvironmentNotRead, ignored.AdministratorNote);
        Assert.DoesNotContain("relay", ignored.AdministratorNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildWithAnAddressOrADevelopmentBuildHasNothingToSayToTheAdministrator()
    {
        Assert.Null(DeploymentAddresses.Resolve(installed: true, Machine, Environment(Elsewhere, null)).AdministratorNote);
        Assert.Null(DeploymentAddresses.Resolve(installed: false, machine: null, Environment(null, null)).AdministratorNote);
    }

    [Fact]
    public async Task TheDeviceCardAndAnActivationSayAnAdministratorSetsTheAddress()
    {
        using var http = new HttpClient();
        var device = new DeviceSession(http, new DeviceSessionTests.InMemoryCredentials())
        {
            NoBackendNote = DeploymentAddresses.NoMachineAddress,
        };

        var activation = await device.ActivateAsync("ABCD-EFGH", "TECH-01", TestContext.Current.CancellationToken);

        Assert.Equal(DeploymentAddresses.NoMachineAddress, device.Standing);
        Assert.Equal(DeploymentAddresses.NoMachineAddress, activation.Refusal);
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
    [InlineData(@"/Program Files/dotnet/dotnet.exe", false)]
    [InlineData(@"/Program Files (x86)/ScreenTail/ScreenTail.Service.exe", false)]
    [InlineData(@"/Program Files/ScreenTailExtra/ScreenTail.Service.exe", false)]
    [InlineData(@"/Users/tech/AppData/Local/ScreenTail.Dev/demo/app/ScreenTail.Service.exe", false)]
    [InlineData(@"/Program Files/ScreenTail/../../Users/tech/ScreenTail.Service.exe", false)]
    [InlineData(null, false)]
    public void OnlyAnExecutableInsideTheInstallFolderCountsAsInstalled(string? executable, bool installed)
    {
        // By location, not by signature: any signer would pass a signature check, the .NET host under
        // Program Files included, and so would a test host running in-process.
        Assert.Equal(installed, DeploymentAddresses.InstalledAt(executable, "/Program Files"));
    }

    [Theory]
    [InlineData(@"/Users/tech/Downloads/ScreenTail/ScreenTail.Service.exe", true, true)]
    [InlineData(@"/Users/tech/Downloads/ScreenTail/screentail.service.EXE", true, true)]
    [InlineData(@"/Users/tech/Downloads/ScreenTail/ScreenTail.Service.exe", false, false)]
    [InlineData(@"/Program Files/dotnet/dotnet.exe", true, false)]
    [InlineData(@"/Users/tech/dotnet/dotnet.exe", true, false)]
    [InlineData(@"/Users/tech/Downloads/ScreenTail/Other.exe", true, false)]
    [InlineData(@"/Program Files/ScreenTail/ScreenTail.Service.exe", false, true)]
    [InlineData(null, true, false)]
    public void TheSignedServiceExecutableCountsAsInstalledWhereverItRuns(string? processPath, bool carriesSignature, bool installed)
    {
        // A copy of the signed release taken out of Program Files into a folder the technician can write
        // must not read SCREENTAIL_BACKEND (2026-11-15 re-review). The .NET host is signed too, so only the
        // service's own executable counts: `dotnet ScreenTail.Service.dll` is never installed, and an
        // unsigned development build keeps the variables.
        Assert.Equal(installed, DeploymentAddresses.Installed(processPath, "/Program Files", _ => carriesSignature));
    }

    [Fact]
    public void TheSignatureIsAskedOnlyOfTheServicesOwnExecutable()
    {
        var asked = new List<string>();
        bool Signed(string path)
        {
            asked.Add(path);
            return true;
        }

        _ = DeploymentAddresses.Installed("/Users/tech/dotnet/dotnet.exe", "/Program Files", Signed);
        _ = DeploymentAddresses.Installed("/Program Files/ScreenTail/ScreenTail.Service.exe", "/Program Files", Signed);
        _ = DeploymentAddresses.Installed("/Users/tech/ScreenTail.Service.exe", "/Program Files", Signed);

        Assert.Equal(["/Users/tech/ScreenTail.Service.exe"], asked);
    }

    [Fact]
    public void WithNoProgramFilesNothingCountsAsInstalled()
    {
        Assert.False(DeploymentAddresses.InstalledAt("/Program Files/ScreenTail/ScreenTail.Service.exe", null));
        Assert.False(DeploymentAddresses.InstalledAt("/Program Files/ScreenTail/ScreenTail.Service.exe", string.Empty));
    }

    private static Func<string, string?> Environment(string? backend, string? token) => name => name switch
    {
        DeploymentAddresses.BackendVariable => backend,
        DeploymentAddresses.DeviceTokenVariable => token,
        _ => null,
    };
}
