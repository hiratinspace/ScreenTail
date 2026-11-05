using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Store;
using ScreenTail.Service.Host;
using ScreenTail.Service.Store;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// Where the capture service keeps its files (ST-112, INV-12). The product's set is the one the service has
/// always used, and whatever folder a set names, "Delete everything" reaches its secrets, because they are
/// derived from the folder under the names the eraser removes.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class CaptureHostPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", "paths-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheServicesOwnSetIsTheProductsFolderAndPipe()
    {
        var paths = CaptureHostPaths.Default();

        Assert.Equal(CaptureHost.DataDirectory, paths.DataDirectory);
        Assert.Equal(DpapiKeyProvider.DefaultKeyFilePath, paths.KeyFile);
        Assert.Equal(DpapiDeviceCredentials.DefaultPath, paths.DeviceCredentials);
        Assert.Equal(IpcTokenFile.DefaultPath, paths.IpcTokenFile);
        Assert.Equal(ScreenTail.Platform.Ipc.WindowsPipeFactory.PipeNameForCurrentUser(), paths.PipeName);
        Assert.Null(paths.ModelDirectory);
    }

    [Fact]
    public void DeleteEverythingReachesTheKeyTheCredentialAndTheTokenOfAnySet()
    {
        var paths = new CaptureHostPaths(Path.Combine(_dir, ".", "data"), "unused", ModelDirectory: null);
        _ = Directory.CreateDirectory(paths.DataDirectory);
        foreach (var secret in new[] { paths.KeyFile, paths.DeviceCredentials, paths.IpcTokenFile })
        {
            Assert.Equal(paths.DataDirectory, Path.GetDirectoryName(secret));
            File.WriteAllBytes(secret, [1, 2, 3]);
        }

        var erased = LocalDataEraser.Erase(paths.DataDirectory);

        Assert.True(erased.Complete);
        Assert.False(File.Exists(paths.KeyFile), "the store's key survived Delete everything");
        Assert.False(File.Exists(paths.DeviceCredentials), "the device's credential survived Delete everything");
        Assert.False(File.Exists(paths.IpcTokenFile), "the IPC token survived Delete everything");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing secret is left in it: the test above erased it.
        }
    }
}
