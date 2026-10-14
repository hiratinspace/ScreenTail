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
    public void ClearingTheCredentialStillTakesTheCopyWhenTheTokenIsHeldOpen()
    {
        // Both files hold the token. One that will not go is no reason to leave the other behind; the
        // failure is still reported, so the caller knows the erasure is owed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var path = Path.Combine(_directory, "device.cred");
        var credentials = new DpapiDeviceCredentials(path);
        credentials.Save(new DeviceCredential("refresh-secret-1234", "Acme IT", Guid.NewGuid(), DateTimeOffset.UnixEpoch));
        File.Copy(path, path + ".tmp");

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(credentials.Clear);
            Assert.False(File.Exists(path + ".tmp"));
        }

        credentials.Clear();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AReadOnlyFileOrFolderDoesNotLeaveTheErasureOwedForever()
    {
        // Windows will not delete anything marked read-only, and a restore from backup or a copy off
        // removable media can leave that mark on anything. An erasure refused over it would stay owed,
        // and every later start would run it again, after the technician had activated the device anew
        // and recorded new sessions. The mark belongs to the same account as the files, so it is cleared.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        LocalDataEraser.MarkPending(_directory);
        var nested = Directory.CreateDirectory(Path.Combine(_directory, "crashes", "older"));
        foreach (var file in (string[])["store.key", "settings.json", Path.Combine("crashes", "report.txt"), Path.Combine("crashes", "older", "report.txt")])
        {
            var path = Path.Combine(_directory, file);
            File.WriteAllText(path, "a customer's session");
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        nested.Attributes |= FileAttributes.ReadOnly;
        nested.Parent!.Attributes |= FileAttributes.ReadOnly;

        var result = Assert.IsType<ErasureResult>(LocalDataEraser.EraseIfPending(_directory));

        Assert.True(result.Complete);
        Assert.False(LocalDataEraser.IsPending(_directory));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
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
            // A failed run can leave read-only entries behind, which would otherwise fail the clean-up too.
            foreach (var entry in new DirectoryInfo(_directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            Directory.Delete(_directory, recursive: true);
        }
    }
}
