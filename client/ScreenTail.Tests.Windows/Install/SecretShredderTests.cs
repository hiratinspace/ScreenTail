using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Install;
using ScreenTail.Service.Install;

namespace ScreenTail.Tests.Windows.Install;

/// <summary>
/// The uninstall's removal of a signed-out technician's key and credentials, against the real file
/// system (ST-112). The installer smoke test checks it end to end on a fresh profile, where a link is
/// caught earlier, by the plan; these call the shredder itself, so its own refusals are what is tested:
/// a plain secret file is removed (read-only too), and a file with a second name, a file that is a
/// link, and a link at any folder between the profile and the data are all left, and so is whatever
/// they point at.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretShredderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screentail-tests", "shred-" + Guid.NewGuid().ToString("N"));

    private string Profile => Path.Combine(_root, "profile");

    private string Outside => Path.Combine(_root, "outside");

    private string DataFolder => Path.Combine([Profile, .. InstalledFootprint.DataSegments]);

    [Fact]
    public void PlainSecretFilesAreRemovedAndTheStoreStays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(Path.Combine(DataFolder, "store.key"), "key");
        File.WriteAllText(Path.Combine(DataFolder, "device.cred"), "credential");
        File.WriteAllText(Path.Combine(DataFolder, "store.db"), "store");

        var results = SecretShredder.Shred(Profile);

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
    public void AReadOnlyKeyIsRemovedToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Directory.CreateDirectory(DataFolder);
        var key = Path.Combine(DataFolder, "store.key");
        File.WriteAllText(key, "key");
        File.SetAttributes(key, FileAttributes.ReadOnly);

        var results = SecretShredder.Shred(Profile);

        Assert.Equal(new SecretShredder.Result(SecretShredder.Outcome.Removed, null), results[0]);
        Assert.False(File.Exists(key));
    }

    [Fact]
    public void AKeyWithASecondNameIsLeftInDoubt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Directory.CreateDirectory(DataFolder);
        var key = Path.Combine(DataFolder, "store.key");
        File.WriteAllText(key, "key");
        var elsewhere = Path.Combine(Profile, "elsewhere.key");
        Assert.True(CreateHardLinkW(elsewhere, key, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

        var results = SecretShredder.Shred(Profile);

        Assert.Equal(new SecretShredder.Result(SecretShredder.Outcome.LeftInDoubt, "hard-linked"), results[0]);
        Assert.True(File.Exists(key));
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void AKeyThatIsALinkIsLeftAndWhatItPointsAtSurvives()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(Outside);
        var target = Path.Combine(Outside, "target.key");
        File.WriteAllText(target, "not ours");
        var key = Path.Combine(DataFolder, "store.key");
        try
        {
            File.CreateSymbolicLink(key, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex.HResult == unchecked((int)0x80070522))
        {
            // ERROR_PRIVILEGE_NOT_HELD: this account may not make file links (hosted runners can). Not a
            // skip, because every skip budget is spent: what can still be checked is checked, and the run
            // says plainly that the link case itself was not exercised here.
            Assert.False(File.Exists(key));
            Assert.Equal(SecretShredder.Outcome.Absent, SecretShredder.Shred(Profile)[0].Outcome);
            Assert.Equal("not ours", File.ReadAllText(target));
            Measurements.Record("SecretShredderTests: the symbolic-link key case was NOT exercised; this account may not create symbolic links.");
            return;
        }

        var results = SecretShredder.Shred(Profile);

        Assert.Equal(new SecretShredder.Result(SecretShredder.Outcome.LeftInDoubt, "link-or-directory"), results[0]);
        Assert.True(File.Exists(target));
        Assert.Equal("not ours", File.ReadAllText(target));
        Assert.True(File.GetAttributes(key).HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>Each folder between the profile and the data, in turn, is a junction to a folder outside.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AJunctionAtAnyFolderLeavesEverythingAndWhatItPointsAtSurvives(int linked)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var segments = InstalledFootprint.DataSegments;
        Assert.Equal(3, segments.Count);

        // The real folders down to the one that is a link, and outside, the rest of the path with a key in it.
        var above = Path.Combine([Profile, .. segments.Take(linked)]);
        Directory.CreateDirectory(above);
        var target = Path.Combine(Outside, "target");
        var outsideData = Path.Combine([target, .. segments.Skip(linked + 1)]);
        Directory.CreateDirectory(outsideData);
        var outsideKey = Path.Combine(outsideData, "store.key");
        File.WriteAllText(outsideKey, "not ours");
        CreateJunction(Path.Combine(above, segments[linked]), target);
        Assert.True(File.Exists(Path.Combine(DataFolder, "store.key")), "the junction does not lead to the key");

        var results = SecretShredder.Shred(Profile);

        Assert.All(results, result => Assert.Equal(new SecretShredder.Result(SecretShredder.Outcome.LeftInDoubt, "dir-not-a-plain-directory"), result));
        Assert.True(File.Exists(outsideKey));
        Assert.Equal("not ours", File.ReadAllText(outsideKey));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Links first, so deleting the tree never reaches through one.
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Where(p => File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)).ToList())
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd did not start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed ({process.ExitCode}): {output}");
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "mklink made no junction");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}
