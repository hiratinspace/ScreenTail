using System.Runtime.Versioning;
using ScreenTail.Core.Net;
using ScreenTail.Service.Store;

namespace ScreenTail.Tests.Windows.Store;

/// <summary>The refresh token on disk, under DPAPI for this Windows user (ST-010), the way the store key is.</summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceCredentialsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.cred");

    [Fact]
    public void TheCredentialRoundTripsAndIsNotPlainOnDisk()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var credentials = new DpapiDeviceCredentials(_path);
        var saved = new DeviceCredential("refresh-secret-1234", "Acme IT", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        credentials.Save(saved);
        var loaded = credentials.Load();

        Assert.Equal(saved, loaded);
        Assert.DoesNotContain("refresh-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(_path)), StringComparison.Ordinal);
        credentials.Clear();
        Assert.Null(credentials.Load());
        Assert.False(File.Exists(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
