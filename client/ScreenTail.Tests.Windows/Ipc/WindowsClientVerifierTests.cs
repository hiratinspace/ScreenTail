using System.Runtime.Versioning;
using ScreenTail.Service.Host;
using ScreenTail.Service.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>ST-004 AC2 on Windows: the executable rule behind "an unsigned process is rejected" (ADR-0003).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClientVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void SignedServiceAcceptsSamePublisherAndRejectsUnsigned()
    {
        // dotnet.exe (the test host) is signed by Microsoft; the test assembly is not signed at all.
        var verifier = new WindowsClientVerifier(Environment.ProcessPath!);
        Assert.True(verifier.ServiceIsSigned);

        Assert.Null(verifier.Verify(Environment.ProcessPath!));
        Assert.Equal("unsigned_or_other_publisher", verifier.Verify(typeof(WindowsClientVerifierTests).Assembly.Location));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void UnsignedServiceAcceptsSiblingsOnly()
    {
        Directory.CreateDirectory(_dir);
        var unsignedService = Path.Combine(_dir, "ScreenTail.Service.exe");
        File.Copy(typeof(WindowsClientVerifierTests).Assembly.Location, unsignedService);
        var sibling = Path.Combine(_dir, "ScreenTail.UI.exe");
        File.WriteAllBytes(sibling, [0x4D, 0x5A]);
        var verifier = new WindowsClientVerifier(unsignedService);

        Assert.False(verifier.ServiceIsSigned);
        Assert.Null(verifier.Verify(sibling));
        Assert.Equal("dev_build_outside_service_directory", verifier.Verify(Environment.ProcessPath!));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void LoginStartupRegistersAndUnregisters()
    {
        var valueName = "ScreenTail.Test." + Guid.NewGuid().ToString("N");
        try
        {
            LoginStartup.Register(valueName, Environment.ProcessPath!, "--test");
            Assert.True(LoginStartup.IsRegistered(valueName));
        }
        finally
        {
            LoginStartup.Unregister(valueName);
        }

        Assert.False(LoginStartup.IsRegistered(valueName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
