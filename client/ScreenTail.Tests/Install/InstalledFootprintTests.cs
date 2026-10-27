using ScreenTail.Core.Install;

namespace ScreenTail.Tests.Install;

/// <summary>
/// ST-112: what an uninstall removes besides the files the installer put down, and what it must leave
/// alone (INV-12, ADR-0007).
///
/// The installer is per machine, so the uninstall runs once for the whole machine, as the system account,
/// and has to find every technician's data folder and every Run value the UI wrote for "Start UI at
/// login" on its own. These are Windows paths handled as text, so the rules hold on every machine the
/// suite runs on, not only the one that runs the uninstall.
/// </summary>
public sealed class InstalledFootprintTests
{
    private const string InstallDirectory = @"C:\Program Files\ScreenTail\";

    [Fact]
    public void EveryProfileHasItsDataFolderUnderLocalAppData()
    {
        var directories = InstalledFootprint.DataDirectories([@"C:\Users\ana", @"D:\Profiles\ben"]);

        Assert.Equal([@"C:\Users\ana\AppData\Local\ScreenTail", @"D:\Profiles\ben\AppData\Local\ScreenTail"], directories);
    }

    [Fact]
    public void AProfilePathIsReadTheWayWindowsReadsIt()
    {
        // A trailing separator, forward slashes and a doubled separator all name the same folder, and the
        // same folder twice (Windows paths are case-insensitive) is one folder to remove, not two.
        var directories = InstalledFootprint.DataDirectories([@"C:\Users\ana\", "C:/Users/ana", @"c:\users\ANA", @"C:\Users\\ben"]);

        Assert.Equal([@"C:\Users\ana\AppData\Local\ScreenTail", @"C:\Users\ben\AppData\Local\ScreenTail"], directories);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Users\ana")]
    [InlineData(@"%SystemDrive%\Users\ana")]
    [InlineData(@"\\server\profiles\ana")]
    [InlineData(@"C:\Users\..\Windows")]
    [InlineData(@"C:\Users\.\ana")]
    [InlineData(@"C:\Users\ana:stream")]
    [InlineData("C:\\Users\\a\"na")]
    [InlineData(@"C:")]
    [InlineData(@"C:\")]
    public void AProfilePathThatIsNotAPlainLocalFolderIsNotGuessedAt(string? profile)
    {
        // The uninstall deletes what this returns, as the system account. A path it would have to
        // interpret (unexpanded, relative, on another machine, climbing out with "..", naming an
        // alternate stream) is skipped rather than resolved, so nothing it removes is a guess.
        Assert.Empty(InstalledFootprint.DataDirectories([profile]));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\ScreenTail\\ScreenTail.UI.exe\"", @"C:\Program Files\ScreenTail\ScreenTail.UI.exe")]
    [InlineData("\"C:\\Program Files\\ScreenTail\\ScreenTail.UI.exe\" --minimised", @"C:\Program Files\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTail\ScreenTail.UI.exe --minimised", @"C:\Program Files\ScreenTail\ScreenTail.UI.exe")]
    [InlineData(@"C:\Tools\tool.EXE", @"C:\Tools\tool.EXE")]
    [InlineData(@"C:\Tools\tool -x", @"C:\Tools\tool")]
    [InlineData("  \"C:\\a b\\c.exe\"  ", @"C:\a b\c.exe")]
    public void TheExecutableIsReadFromTheCommandTheWayTheRunKeyHoldsIt(string command, string expected)
    {
        Assert.Equal(expected, InstalledFootprint.ExecutableOf(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"C:\\Program Files\\ScreenTail\\ScreenTail.UI.exe")]
    public void ACommandWithNoExecutableNamesNone(string? command)
    {
        Assert.Null(InstalledFootprint.ExecutableOf(command));
    }

    [Theory]
    [InlineData(@"C:\Program Files\ScreenTail\ScreenTail.UI.exe", true)]
    [InlineData(@"c:\program files\screentail\runtimes\win-x64\whisper.dll", true)]
    [InlineData("C:/Program Files/ScreenTail/ScreenTail.Service.exe", true)]
    [InlineData(@"C:\Program Files\ScreenTail", false)]
    [InlineData(@"C:\Program Files\ScreenTail.Dev\ScreenTail.UI.exe", false)]
    [InlineData(@"C:\Program Files\ScreenTailOld\ScreenTail.UI.exe", false)]
    [InlineData(@"C:\Program Files\ScreenTail\..\Other\tool.exe", false)]
    [InlineData(@"D:\Program Files\ScreenTail\ScreenTail.UI.exe", false)]
    [InlineData(null, false)]
    public void OnlyWhatIsInsideTheInstallFolderCounts(string? path, bool inside)
    {
        // A sibling whose name starts the same way is somebody else's folder: a prefix match would have
        // taken the developer's ScreenTail.Dev with it.
        Assert.Equal(inside, InstalledFootprint.IsInside(path, InstallDirectory));
    }

    [Fact]
    public void TheRunValuesRemovedAreTheOnesThatStartSomethingInstalledHere()
    {
        // The UI's own "Start UI at login" value goes, whatever it is called. A developer's LoginStartup
        // value pointing at a build folder, and every other program's value, stay: the uninstall removes
        // what this installation started, not everything that looks like ScreenTail.
        var values = new Dictionary<string, string?>
        {
            ["ScreenTail"] = "\"C:\\Program Files\\ScreenTail\\ScreenTail.UI.exe\"",
            ["Renamed"] = @"C:\Program Files\ScreenTail\ScreenTail.UI.exe --tray",
            ["ScreenTail.Service"] = "\"C:\\Users\\ana\\AppData\\Local\\ScreenTail.Dev\\run\\ScreenTail.Service.exe\"",
            ["OneDrive"] = "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background",
            ["Empty"] = null,
        };

        var removed = InstalledFootprint.RunValuesToRemove(values, InstallDirectory);

        Assert.Equal(["Renamed", "ScreenTail"], removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnInstallFolderThatIsNotAPlainLocalFolderMatchesNothing()
    {
        // The uninstall passes its own folder. If that ever arrives as something it cannot read plainly,
        // the answer is "nothing is ours", never "everything is".
        var values = new Dictionary<string, string?> { ["ScreenTail"] = @"C:\Program Files\ScreenTail\ScreenTail.UI.exe" };

        Assert.Empty(InstalledFootprint.RunValuesToRemove(values, @"Program Files\ScreenTail"));
        Assert.Empty(InstalledFootprint.RunValuesToRemove(values, @"C:\"));
        Assert.False(InstalledFootprint.IsInside(@"C:\Windows\notepad.exe", @"C:\"));
    }
}
