using ScreenTrail.Core.Install;

namespace ScreenTrail.Tests.Install;

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
    private const string InstallDirectory = @"C:\Program Files\ScreenTrail\";

    [Fact]
    public void EveryProfileHasItsDataFolderUnderLocalAppData()
    {
        var folders = Folders(@"C:\Users\ana", @"D:\Profiles\ben");

        Assert.Equal([@"C:\Users\ana\AppData\Local\ScreenTrail", @"D:\Profiles\ben\AppData\Local\ScreenTrail"], folders);
    }

    [Fact]
    public void AProfilePathIsReadTheWayWindowsReadsIt()
    {
        // A trailing separator, forward slashes and a doubled separator all name the same folder.
        var folders = Folders(@"C:\Users\ana\", "C:/Users/ana", @"C:\Users\\ben");

        Assert.Equal([@"C:\Users\ana\AppData\Local\ScreenTrail", @"C:\Users\ana\AppData\Local\ScreenTrail", @"C:\Users\ben\AppData\Local\ScreenTrail"], folders);
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
        // A path the uninstall would have to interpret (unexpanded, relative, on another machine,
        // climbing out with "..", naming an alternate stream) is left alone rather than resolved, so
        // nothing it touches is a guess.
        var decision = Assert.Single(InstalledFootprint.PlanDataRemoval([new("S-1-5-21-1", profile)], new HashSet<string>(["S-1-5-21-1"]), _ => PathState.Folder));

        Assert.Equal(DataFolderAction.LeaveUnreadablePath, decision.Action);
        Assert.Null(decision.Folder);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\ScreenTrail\\ScreenTrail.UI.exe\"", @"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe")]
    [InlineData("\"C:\\Program Files\\ScreenTrail\\ScreenTrail.UI.exe\" --minimised", @"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe")]
    [InlineData(@"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe --minimised", @"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe")]
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
    [InlineData("\"C:\\Program Files\\ScreenTrail\\ScreenTrail.UI.exe")]
    public void ACommandWithNoExecutableNamesNone(string? command)
    {
        Assert.Null(InstalledFootprint.ExecutableOf(command));
    }

    [Theory]
    [InlineData(@"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe", true)]
    [InlineData(@"c:\program files\screentrail\runtimes\win-x64\whisper.dll", true)]
    [InlineData("C:/Program Files/ScreenTrail/ScreenTrail.Service.exe", true)]
    [InlineData(@"C:\Program Files\ScreenTrail", false)]
    [InlineData(@"C:\Program Files\ScreenTrail.Dev\ScreenTrail.UI.exe", false)]
    [InlineData(@"C:\Program Files\ScreenTrailOld\ScreenTrail.UI.exe", false)]
    [InlineData(@"C:\Program Files\ScreenTrail\..\Other\tool.exe", false)]
    [InlineData(@"D:\Program Files\ScreenTrail\ScreenTrail.UI.exe", false)]
    [InlineData(null, false)]
    public void OnlyWhatIsInsideTheInstallFolderCounts(string? path, bool inside)
    {
        // A sibling whose name starts the same way is somebody else's folder: a prefix match would have
        // taken the developer's ScreenTrail.Dev with it.
        Assert.Equal(inside, InstalledFootprint.IsInside(path, InstallDirectory));
    }

    [Fact]
    public void TheRunValuesRemovedAreTheOnesThatStartSomethingInstalledHere()
    {
        // The UI's own "Start UI at login" value goes, whatever it is called. A developer's LoginStartup
        // value pointing at a build folder, and every other program's value, stay: the uninstall removes
        // what this installation started, not everything that looks like ScreenTrail.
        var values = new Dictionary<string, string?>
        {
            ["ScreenTrail"] = "\"C:\\Program Files\\ScreenTrail\\ScreenTrail.UI.exe\"",
            ["Renamed"] = @"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe --tray",
            ["ScreenTrail.Service"] = "\"C:\\Users\\ana\\AppData\\Local\\ScreenTrail.Dev\\run\\ScreenTrail.Service.exe\"",
            ["OneDrive"] = "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background",
            ["Empty"] = null,
        };

        var removed = InstalledFootprint.RunValuesToRemove(values, InstallDirectory);

        Assert.Equal(["Renamed", "ScreenTrail"], removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnInstallFolderThatIsNotAPlainLocalFolderMatchesNothing()
    {
        // The uninstall passes its own folder. If that ever arrives as something it cannot read plainly,
        // the answer is "nothing is ours", never "everything is".
        var values = new Dictionary<string, string?> { ["ScreenTrail"] = @"C:\Program Files\ScreenTrail\ScreenTrail.UI.exe" };

        Assert.Empty(InstalledFootprint.RunValuesToRemove(values, @"Program Files\ScreenTrail"));
        Assert.Empty(InstalledFootprint.RunValuesToRemove(values, @"C:\"));
        Assert.False(InstalledFootprint.IsInside(@"C:\Windows\notepad.exe", @"C:\"));
    }

    private static string?[] Folders(params string[] profiles) =>
        [
            .. InstalledFootprint.PlanDataRemoval(
                profiles.Select((p, i) => new KeyValuePair<string, string?>($"S-1-5-21-{i}", p)),
                new HashSet<string>(),
                _ => PathState.Folder).Select(d => d.Folder),
        ];
}
