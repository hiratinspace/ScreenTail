using ScreenTail.Core.Install;

namespace ScreenTail.Tests.Install;

/// <summary>
/// ST-112: which technicians' data folders an uninstall deletes, and as whom (INV-12, ADR-0007).
///
/// The uninstall runs as the system account, and a data folder sits in a profile its owner controls. So
/// the system account never deletes a folder there: a folder is deleted only for a user who is signed in,
/// with that user's own rights. For a user who is not signed in, the system account removes only the
/// store's key and the device's credentials, one file at a time through handles that refuse links, which
/// leaves what remains of the store unreadable. A link anywhere between the profile and the data folder
/// means nothing there is touched, and everything left is counted.
/// </summary>
public sealed class DataRemovalPlanTests
{
    private const string AnaSid = "S-1-5-21-1-2-3-1001";
    private const string BenSid = "S-1-12-1-4-5-6-7";

    private static readonly Dictionary<string, PathState> Clean = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Users\ana\AppData"] = PathState.Folder,
        [@"C:\Users\ana\AppData\Local"] = PathState.Folder,
        [@"C:\Users\ana\AppData\Local\ScreenTail"] = PathState.Folder,
        [@"C:\Users\ben\AppData"] = PathState.Folder,
        [@"C:\Users\ben\AppData\Local"] = PathState.Folder,
        [@"C:\Users\ben\AppData\Local\ScreenTail"] = PathState.Folder,
    };

    [Fact]
    public void ASignedInUsersFolderIsDeletedAsThatUser()
    {
        var plan = Plan(signedIn: [AnaSid], Clean);

        var ana = Assert.Single(plan, d => d.Sid == AnaSid);
        Assert.Equal(DataFolderAction.DeleteAsOwner, ana.Action);
        Assert.Equal(@"C:\Users\ana\AppData\Local\ScreenTail", ana.Folder);
    }

    [Fact]
    public void AUserWhoIsNotSignedInHasOnlyTheirKeyAndCredentialsRemoved()
    {
        // There is no token to delete the folder with, and the system account does not delete folders
        // in somebody else's profile. Removing the key makes the store that stays unreadable.
        var plan = Plan(signedIn: [AnaSid], Clean);

        var ben = Assert.Single(plan, d => d.Sid == BenSid);
        Assert.Equal(DataFolderAction.ShredSecrets, ben.Action);
    }

    [Fact]
    public void NobodySignedInMeansNoFolderIsDeleted()
    {
        // An RMM uninstall at night, with every technician signed out.
        var plan = Plan(signedIn: [], Clean);

        Assert.All(plan, d => Assert.Equal(DataFolderAction.ShredSecrets, d.Action));
    }

    [Fact]
    public void TheFilesRemovedForAUserWhoIsNotSignedInAreTheErasersSecretsInItsOrder()
    {
        // Delete everything's own list (LocalDataEraser): the store's key first, because without it the
        // store is unreadable, then the device's refresh token and the file a save writes before it, then
        // the pipe token. Read from the eraser's source so the two cannot drift apart.
        var eraser = File.ReadAllText(Repo("client", "ScreenTail.Core", "Store", "RetentionJob.cs"));

        Assert.Contains("SecretsFirst = [\"store.key\", \"device.cred\", \"device.cred.tmp\"]", eraser, StringComparison.Ordinal);
        Assert.Contains("[.. SecretsFirst, \"ipc.token\",", eraser, StringComparison.Ordinal);
        Assert.Equal(["store.key", "device.cred", "device.cred.tmp", "ipc.token"], InstalledFootprint.Secrets);
    }

    [Fact]
    public void TheDataFolderIsReachedOneFolderAtATime()
    {
        // The system-side removal opens each of these relative to the one before, never a whole path.
        Assert.Equal(["AppData", "Local", "ScreenTail"], InstalledFootprint.DataSegments);
        Assert.All(InstalledFootprint.Secrets.Concat(InstalledFootprint.DataSegments), name => Assert.DoesNotContain('\\', name));
    }

    [Theory]
    [InlineData(@"C:\Users\ana\AppData")]
    [InlineData(@"C:\Users\ana\AppData\Local")]
    [InlineData(@"C:\Users\ana\AppData\Local\ScreenTail")]
    public void ALinkAnywhereInThePathMeansTheFolderIsNotDeleted(string link)
    {
        var states = new Dictionary<string, PathState>(Clean, StringComparer.OrdinalIgnoreCase) { [link] = PathState.Link };

        var plan = Plan(signedIn: [AnaSid, BenSid], states);

        Assert.Equal(DataFolderAction.LeaveLinked, Assert.Single(plan, d => d.Sid == AnaSid).Action);
        Assert.Equal(DataFolderAction.DeleteAsOwner, Assert.Single(plan, d => d.Sid == BenSid).Action);
    }

    [Fact]
    public void AFolderThatIsNotThereNeedsNothing()
    {
        var states = new Dictionary<string, PathState>(Clean, StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\ana\AppData\Local\ScreenTail"] = PathState.Missing,
        };

        var plan = Plan(signedIn: [AnaSid], states);

        Assert.Equal(DataFolderAction.Absent, Assert.Single(plan, d => d.Sid == AnaSid).Action);
    }

    [Fact]
    public void AProfilePathThatCannotBeReadPlainlyIsLeftAlone()
    {
        var plan = InstalledFootprint.PlanDataRemoval(
            [new(AnaSid, @"%SystemDrive%\Users\ana"), new(BenSid, null)],
            new HashSet<string>([AnaSid, BenSid], StringComparer.OrdinalIgnoreCase),
            _ => PathState.Folder);

        Assert.All(plan, d =>
        {
            Assert.Equal(DataFolderAction.LeaveUnreadablePath, d.Action);
            Assert.Null(d.Folder);
        });
    }

    [Fact]
    public void OnlyTheFoldersBelowTheProfileAreAskedAbout()
    {
        // The profile folder itself belongs to Windows; everything under it belongs to the user. The
        // probe is asked about exactly the folders a user could replace with a link.
        var asked = new List<string>();
        _ = InstalledFootprint.PlanDataRemoval(
            [new(AnaSid, @"C:\Users\ana")],
            new HashSet<string>([AnaSid], StringComparer.OrdinalIgnoreCase),
            path =>
            {
                asked.Add(path);
                return PathState.Folder;
            });

        Assert.Equal([@"C:\Users\ana\AppData", @"C:\Users\ana\AppData\Local", @"C:\Users\ana\AppData\Local\ScreenTail"], asked);
    }

    private static string Repo(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{string.Join('/', parts)} was not found above {AppContext.BaseDirectory}.");
    }

    private static IReadOnlyList<DataFolderDecision> Plan(string[] signedIn, Dictionary<string, PathState> states) =>
        InstalledFootprint.PlanDataRemoval(
            [new(AnaSid, @"C:\Users\ana"), new(BenSid, @"C:\Users\ben")],
            new HashSet<string>(signedIn, StringComparer.OrdinalIgnoreCase),
            path => states.TryGetValue(path, out var state) ? state : PathState.Missing);
}
