using ScreenTrail.Core.Install;
using ScreenTrail.Shared.Settings;

namespace ScreenTrail.Tests.Settings;

/// <summary>
/// The product was ScreenTail until 2026-11-17. Its data folder comes across at the first start after
/// the upgrade, whole when it can be, and nothing in it is ever lost on the way.
/// </summary>
public sealed class LegacyDataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screentrail-tests", Guid.NewGuid().ToString("N"));

    public LegacyDataFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Old => Path.Combine(_root, "ScreenTail");

    private string New => Path.Combine(_root, "ScreenTrail");

    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void WithNoOldFolderNothingHappens()
    {
        Assert.Equal(LegacyDataMigration.NothingToMove, LegacyDataFolder.Migrate(_root));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void TheOldFolderBecomesTheNewOneWithEverythingInIt()
    {
        Write(Old, "store.db", "store");
        Write(Old, "store.key", "key");
        Write(Old, "device.cred", "cred");
        Write(Old, Path.Combine("models", "ggml-base.en.bin"), "model");

        Assert.Equal(LegacyDataMigration.Moved, LegacyDataFolder.Migrate(_root));

        Assert.False(Directory.Exists(Old));
        Assert.Equal("store", File.ReadAllText(Path.Combine(New, "store.db")));
        Assert.Equal("key", File.ReadAllText(Path.Combine(New, "store.key")));
        Assert.Equal("cred", File.ReadAllText(Path.Combine(New, "device.cred")));
        Assert.Equal("model", File.ReadAllText(Path.Combine(New, "models", "ggml-base.en.bin")));
    }

    [Fact]
    public void ASecondStartFindsNothingLeftToMove()
    {
        Write(Old, "store.db", "store");
        Assert.Equal(LegacyDataMigration.Moved, LegacyDataFolder.Migrate(_root));
        Assert.Equal(LegacyDataMigration.NothingToMove, LegacyDataFolder.Migrate(_root));
    }

    [Fact]
    public void WhenTheWindowMadeTheNewFolderFirstTheRestComesAcrossAndWhatItWroteStays()
    {
        // The window started first and wrote shell.json; the service's store is still under the old name.
        Write(New, "shell.json", "new shell");
        Write(Old, "shell.json", "old shell");
        Write(Old, "store.db", "store");
        Write(Old, Path.Combine("models", "ggml-base.en.bin"), "model");

        Assert.Equal(LegacyDataMigration.LeftBehind, LegacyDataFolder.Migrate(_root));

        Assert.Equal("new shell", File.ReadAllText(Path.Combine(New, "shell.json")));
        Assert.Equal("store", File.ReadAllText(Path.Combine(New, "store.db")));
        Assert.Equal("model", File.ReadAllText(Path.Combine(New, "models", "ggml-base.en.bin")));

        // The older copy of what both had is left where it was, never deleted.
        Assert.Equal("old shell", File.ReadAllText(Path.Combine(Old, "shell.json")));
        Assert.False(File.Exists(Path.Combine(Old, "store.db")));
    }

    [Fact]
    public void AMergeThatTakesEverythingRemovesTheEmptyOldFolder()
    {
        Write(New, "shell.json", "new shell");
        Write(Old, "store.db", "store");

        Assert.Equal(LegacyDataMigration.Merged, LegacyDataFolder.Migrate(_root));

        Assert.False(Directory.Exists(Old));
        Assert.Equal("store", File.ReadAllText(Path.Combine(New, "store.db")));
    }

    [Fact]
    public void AnOldFolderThatIsALinkIsNotFollowedOrMoved()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Write(elsewhere, "store.db", "not ours");
        try
        {
            Directory.CreateSymbolicLink(Old, elsewhere);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a link needs a privilege this machine may not give a test; nothing to check then.
            return;
        }

        Assert.Equal(LegacyDataMigration.LeftLinked, LegacyDataFolder.Migrate(_root));

        Assert.False(Directory.Exists(New));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(elsewhere, "store.db")));
    }

    [Fact]
    public void TheNamesAreTheOnesTheUninstallRemoves()
    {
        Assert.Equal([LegacyDataFolder.NewName, LegacyDataFolder.OldName], InstalledFootprint.DataFolderNames);
        Assert.Equal(["AppData", "Local", "ScreenTail"], InstalledFootprint.DataSegmentsFor(LegacyDataFolder.OldName));
        Assert.Equal(InstalledFootprint.DataSegments, InstalledFootprint.DataSegmentsFor(null));
        Assert.Throws<ArgumentException>(() => InstalledFootprint.DataSegmentsFor("Elsewhere"));
    }

    [Fact]
    public void TheUninstallPlansTheOldFolderWithTheSameChecks()
    {
        var profile = @"C:\Users\ana";
        var states = new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\ana\AppData"] = PathState.Folder,
            [@"C:\Users\ana\AppData\Local"] = PathState.Folder,
            [@"C:\Users\ana\AppData\Local\ScreenTail"] = PathState.Folder,
        };

        var decision = Assert.Single(InstalledFootprint.PlanDataRemoval(
            [new("S-1-5-21-1", profile)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S-1-5-21-1" },
            path => states.TryGetValue(path, out var state) ? state : PathState.Missing,
            LegacyDataFolder.OldName));

        Assert.Equal(DataFolderAction.DeleteAsOwner, decision.Action);
        Assert.Equal(@"C:\Users\ana\AppData\Local\ScreenTail", decision.Folder);

        states[@"C:\Users\ana\AppData\Local\ScreenTail"] = PathState.Link;
        Assert.Equal(DataFolderAction.LeaveLinked, InstalledFootprint.PlanDataRemoval(
            [new("S-1-5-21-1", profile)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S-1-5-21-1" },
            path => states.TryGetValue(path, out var state) ? state : PathState.Missing,
            LegacyDataFolder.OldName)[0].Action);
    }
}
