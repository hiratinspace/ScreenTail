using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// The folder comparison both peer checks make (2026-11-15 review). Plain paths, so each way the same
/// folder can be spelt, and each way a different folder can be made to look like it, is one line.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PeerDirectoryTests : IDisposable
{
    private const string Installed = @"C:\Program Files\ScreenTail";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Theory(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    [InlineData(@"C:\Program Files\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"c:\program files\screentail\ScreenTail.UI.exe")]
    [InlineData(@"C:\PROGRAM FILES\SCREENTAIL\SCREENTAIL.UI.EXE")]
    [InlineData(@"C:/Program Files/ScreenTail/ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTail\.\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\Other\..\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"\\?\C:\Program Files\ScreenTail\ScreenTail.UI.exe")]
    public void TheSameFolderSpeltAnotherWayIsTheSameFolder(string ui)
    {
        Assert.True(PeerDirectory.Same(ui, Installed + @"\ScreenTail.Service.exe"));
    }

    [Theory(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    [InlineData(@"C:\Users\tech\Downloads\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTail\sub\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTailX\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTai\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTail.UI.exe")]
    [InlineData(@"D:\Program Files\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"\\server\share\Program Files\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Program Files\ScreenTail\ScreenTail.UI.exe")]
    public void AnyOtherFolderIsNot(string ui)
    {
        Assert.False(PeerDirectory.Same(ui, Installed + @"\ScreenTail.Service.exe"));
    }

    [Theory(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    [InlineData(@"C:\Program Files \ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files.\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTail.\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTail \ScreenTail.UI.exe")]
    [InlineData(@"\\?\C:\Program Files.\ScreenTail\ScreenTail.UI.exe")]
    public void AFolderWhoseNameOnlyAddsATrailingDotOrSpaceIsNotOurs(string ui)
    {
        // A folder at the root of C: can be made by any user, and Windows' path clean-up would read one
        // named with a trailing dot or space as Program Files. The comparison must not.
        Assert.False(PeerDirectory.Same(ui, Installed + @"\ScreenTail.Service.exe"));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AShortNameIsLookedUpInTheFolderAsWrittenNotItsCleanedUpName()
    {
        // A real folder named with a trailing dot, holding a folder literally named like a short name.
        // Expanded through Windows' clean-up, the path would land in the folder without the dot.
        var dotted = @"\\?\" + Path.Combine(_dir, "install.");
        var plain = Path.Combine(_dir, "install");
        Directory.CreateDirectory(Path.Combine(dotted, "SCREEN~1"));
        Directory.CreateDirectory(Path.Combine(plain, "ScreenTail"));

        Assert.False(PeerDirectory.Same(
            Path.Combine(Path.Combine(_dir, "install."), "SCREEN~1", "ScreenTail.UI.exe"),
            Path.Combine(plain, "ScreenTail", "ScreenTail.Service.exe")));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void CaseIsIgnoredForAsciiLettersAndNothingElse()
    {
        Assert.True(PeerDirectory.SameText(@"C:\Program Files\ScreenTail", @"c:\PROGRAM FILES\screentail"));

        // A dotless i, a Kelvin sign for K, a long s: each folds onto an ASCII letter in some casing
        // table, and each is a different folder name to the file system.
        Assert.False(PeerDirectory.SameText(@"C:\Program Fıles\ScreenTail", @"C:\Program Files\ScreenTail"));
        Assert.False(PeerDirectory.SameText("C:\\\u212Aeep", @"C:\Keep"));
        Assert.False(PeerDirectory.SameText("C:\\\u017Fcreen", @"C:\Screen"));

        // Not ASCII, but identical, is the same.
        Assert.True(PeerDirectory.SameText(@"C:\Users\Zoë\ScreenTail", @"C:\Users\Zoë\ScreenTail"));
        Assert.False(PeerDirectory.SameText(@"C:\Users\Zoë\ScreenTail", @"C:\Users\ZOË\ScreenTail"));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AShortFolderNameIsTheFolderItNames()
    {
        var folder = Path.Combine(_dir, "a folder name too long for eight dot three");
        Directory.CreateDirectory(folder);
        var buffer = new char[32_768];
        var length = GetShortPathName(folder, buffer, (uint)buffer.Length);
        Assert.True(length > 0, $"GetShortPathName failed: {Marshal.GetLastPInvokeError()}");
        var shortFolder = new string(buffer, 0, (int)length);
        Assert.SkipWhen(
            string.Equals(Path.GetFileName(shortFolder), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase),
            "8.3 names are not generated on this volume");

        Assert.True(PeerDirectory.Same(Path.Combine(shortFolder, "ScreenTail.UI.exe"), Path.Combine(folder, "ScreenTail.Service.exe")));
        Assert.True(PeerDirectory.Same(Path.Combine(folder, "ScreenTail.UI.exe"), Path.Combine(shortFolder, "ScreenTail.Service.exe")));
        Assert.False(PeerDirectory.Same(Path.Combine(shortFolder, "ScreenTail.UI.exe"), Path.Combine(_dir, "ScreenTail.Service.exe")));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AJunctionToOurFolderIsNotOurFolder()
    {
        // Followed, a link the user made would stand in for the install folder. The comparison reads the
        // path a process was started from, and that path is the link's.
        var real = Path.Combine(_dir, "real");
        var link = Path.Combine(_dir, "link");
        Directory.CreateDirectory(real);
        Assert.True(Junction(link, real), "mklink /J failed");

        Assert.False(PeerDirectory.Same(Path.Combine(link, "ScreenTail.UI.exe"), Path.Combine(real, "ScreenTail.Service.exe")));
        Assert.True(PeerDirectory.Same(Path.Combine(link, "ScreenTail.UI.exe"), Path.Combine(link, "ScreenTail.Service.exe")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }

        // The junction first, by itself, so the recursive delete cannot walk into what it points at.
        var link = Path.Combine(_dir, "link");
        if (Directory.Exists(link))
        {
            Directory.Delete(link);
        }

        // And the folder with a trailing dot by its literal name, which the plain path would not reach.
        var dotted = @"\\?\" + Path.Combine(_dir, "install.");
        if (Directory.Exists(dotted))
        {
            Directory.Delete(dotted, recursive: true);
        }

        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A directory junction, which any user may make (unlike a symbolic link).</summary>
    private static bool Junction(string link, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        process.WaitForExit(TimeSpan.FromSeconds(30));
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint bufferLength);
}
