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

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AnAnchorOnThisProcessIsTheKernelsRecordOfIt()
    {
        // The peer's path is the kernel's, so this process's own must be too: Environment.ProcessPath is
        // the path as launched, which a junction or a mounted folder spells differently (review of #325).
        var kernel = ProcessImage.OwnPath();
        Assert.NotNull(kernel);

        Assert.Equal(kernel, PeerDirectory.Anchor(Environment.ProcessPath!));
        Assert.Equal(kernel, new WindowsClientVerifier(Environment.ProcessPath!).Anchor);
        Assert.Equal(kernel, new WindowsServerVerifier(Environment.ProcessPath!).Anchor);

        // Any other anchor is taken as given.
        var elsewhere = Path.Combine(_dir, "ScreenTail.Service.exe");
        Assert.Equal(elsewhere, PeerDirectory.Anchor(elsewhere));

        // And a peer named the kernel's way, here this process itself, is in the anchor's folder, whether
        // the test host is signed or not (the hosted runner's is, so a missing sibling would not do).
        Assert.Null(new WindowsClientVerifier(Environment.ProcessPath!).Verify(kernel));
        Assert.Null(new WindowsServerVerifier(Environment.ProcessPath!).Verify(kernel));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void APeerStartedThroughALinkIsJudgedByWhatTheKernelRecords()
    {
        // What the kernel records for a process started through a link decides which folder the check
        // sees, so it is measured here rather than assumed. A junction any user can make; a file symbolic
        // link needs an administrator or Developer Mode, and is measured only where it can be made.
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        var realFolder = Path.GetDirectoryName(PipePeer.Executable)!;
        var link = Path.Combine(_dir, "link");
        Directory.CreateDirectory(_dir);
        Assert.True(Junction(link, realFolder), "mklink /J failed");
        Measure("junction", Path.Combine(link, "powershell.exe"), PipePeer.Executable);

        var fileLink = Path.Combine(_dir, "powershell.exe");
        try
        {
            File.CreateSymbolicLink(fileLink, PipePeer.Executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Measurements.Record($"PeerDirectory: a file symbolic link could not be made on this machine ({ex.GetType().Name}), so that case is not measured here");
            return;
        }

        Measure("file symbolic link", fileLink, PipePeer.Executable);
    }

    /// <summary>
    /// Starts PowerShell through <paramref name="launched"/>, records what the kernel says it was started
    /// from, and checks the folder rule follows that record.
    /// </summary>
    private static void Measure(string kind, string launched, string real)
    {
        var start = new ProcessStartInfo(launched) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        try
        {
            using var image = ProcessImage.Of((uint)process.Id);
            Assert.NotNull(image);
            // Folder by folder, so a short name in the temp path is not a difference.
            var asLaunched = PeerDirectory.Same(image.Path, launched);
            var asReal = PeerDirectory.Same(image.Path, real);
            Measurements.Record($"PeerDirectory: a process started through a {kind} is recorded by the kernel {(asLaunched ? "under the link's path" : asReal ? "under the real path" : "under another path")}");
            Assert.True(asLaunched || asReal, $"The kernel recorded {image.Path}");

        }
        finally
        {
            process.Kill();
            _ = process.WaitForExit(TimeSpan.FromSeconds(5));
        }
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
