using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Store;
using ScreenTail.Service.Host;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Logging;

namespace ScreenTail.Tests.Windows.Store;

/// <summary>
/// "Delete everything" against the real DPAPI credential store (INV-12, 2026-10-12 review T-57). The Mac
/// suite proves the sweep with plain files by the producers' names; this proves the names are the ones
/// the service really uses, and that a credential saved the real way does not load afterwards.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class EraseEverythingWindowsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "screentail-tests", "erase-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DeleteEverythingRemovesTheDeviceCredential()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var path = Path.Combine(_directory, Path.GetFileName(DpapiDeviceCredentials.DefaultPath));
        new DpapiDeviceCredentials(path).Save(new DeviceCredential("refresh-secret-1234", "Acme IT", Guid.NewGuid(), DateTimeOffset.UnixEpoch));
        File.WriteAllBytes(path + ".tmp", [0x01, 0x02, 0x03]); // what an interrupted save leaves
        Assert.NotNull(new DpapiDeviceCredentials(path).Load());

        var result = LocalDataEraser.Erase(_directory);

        Assert.True(result.Complete);
        Assert.Null(new DpapiDeviceCredentials(path).Load());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ClearingTheCredentialTakesAnInterruptedSavesCopyToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var path = Path.Combine(_directory, "device.cred");
        var credentials = new DpapiDeviceCredentials(path);
        credentials.Save(new DeviceCredential("refresh-secret-1234", "Acme IT", Guid.NewGuid(), DateTimeOffset.UnixEpoch));
        File.Copy(path, path + ".tmp");

        credentials.Clear();

        Assert.Null(credentials.Load());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void EverythingTheProductKeepsIsInTheDirectoryDeleteEverythingSweeps()
    {
        // The sweep empties one folder. A producer that moved somewhere else would be outside it, and
        // nothing would say so.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var swept = CaptureHost.DataDirectory;

        Assert.Equal(swept, Path.GetDirectoryName(DpapiDeviceCredentials.DefaultPath));
        Assert.Equal(swept, Path.GetDirectoryName(DpapiKeyProvider.DefaultKeyFilePath));
        Assert.Equal(swept, Path.GetDirectoryName(IpcTokenFile.DefaultPath));
        Assert.Equal(swept, Path.GetDirectoryName(ShellPreferencesStore.DefaultPath));
        Assert.Equal(swept, Path.GetDirectoryName(CrashReport.DefaultDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
