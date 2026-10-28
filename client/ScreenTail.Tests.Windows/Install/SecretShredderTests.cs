using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Install;
using ScreenTail.Service.Install;

namespace ScreenTail.Tests.Windows.Install;

/// <summary>
/// The uninstall's removal of a signed-out technician's key and credentials, against the real file
/// system (ST-112). The installer smoke test checks it end to end on a fresh profile; this pins the
/// two answers that decide it: a plain secret file is removed, and a file with a second name is left.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretShredderTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "screentail-tests", "shred-" + Guid.NewGuid().ToString("N"));

    private string DataFolder => Path.Combine([_profile, .. InstalledFootprint.DataSegments]);

    [Fact]
    public void PlainSecretFilesAreRemovedAndTheStoreStays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(Path.Combine(DataFolder, "store.key"), "key");
        File.WriteAllText(Path.Combine(DataFolder, "device.cred"), "credential");
        File.WriteAllText(Path.Combine(DataFolder, "store.db"), "store");

        var results = SecretShredder.Shred(_profile);

        Assert.Equal(InstalledFootprint.Secrets.Count, results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var name = InstalledFootprint.Secrets[i];
            var expected = name is "store.key" or "device.cred" ? SecretShredder.Outcome.Removed : SecretShredder.Outcome.Absent;
            Assert.True(expected == results[i].Outcome, $"{name}: {results[i].Outcome} ({results[i].Reason})");
        }

        Assert.False(File.Exists(Path.Combine(DataFolder, "store.key")));
        Assert.False(File.Exists(Path.Combine(DataFolder, "device.cred")));
        Assert.True(File.Exists(Path.Combine(DataFolder, "store.db")));
    }

    [Fact]
    public void AKeyWithASecondNameIsLeftInDoubt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Directory.CreateDirectory(DataFolder);
        var key = Path.Combine(DataFolder, "store.key");
        File.WriteAllText(key, "key");
        var elsewhere = Path.Combine(_profile, "elsewhere.key");
        Assert.True(CreateHardLinkW(elsewhere, key, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

        var results = SecretShredder.Shred(_profile);

        Assert.Equal(new SecretShredder.Result(SecretShredder.Outcome.LeftInDoubt, "hard-linked"), results[0]);
        Assert.True(File.Exists(key));
        Assert.True(File.Exists(elsewhere));
    }

    public void Dispose()
    {
        if (Directory.Exists(_profile))
        {
            Directory.Delete(_profile, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}
