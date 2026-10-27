using ScreenTail.Core.Install;

namespace ScreenTail.Tests.Install;

/// <summary>
/// ST-112: which technicians' data folders an uninstall deletes, and as whom (INV-12, ADR-0007).
///
/// The uninstall runs as the system account, and a data folder sits in a profile its owner controls. So
/// the rule is that the system account deletes nothing there by itself: a folder is deleted only for a
/// user who is signed in, with that user's own rights, and a link anywhere between the profile and the
/// data folder means it is left alone. Everybody else's folder is left and counted, and the operator doc
/// says that the in-app "Delete everything" is how a technician erases their own data.
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
    public void AUserWhoIsNotSignedInKeepsTheirFolderAndItIsCounted()
    {
        // There is no token to delete it with, and the system account does not delete in somebody
        // else's profile on its own. It is left, and reported as left, not silently skipped.
        var plan = Plan(signedIn: [AnaSid], Clean);

        var ben = Assert.Single(plan, d => d.Sid == BenSid);
        Assert.Equal(DataFolderAction.LeaveNotSignedIn, ben.Action);
    }

    [Fact]
    public void NobodySignedInMeansNothingIsDeleted()
    {
        // An RMM uninstall at night, with every technician signed out.
        var plan = Plan(signedIn: [], Clean);

        Assert.All(plan, d => Assert.NotEqual(DataFolderAction.DeleteAsOwner, d.Action));
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

    private static IReadOnlyList<DataFolderDecision> Plan(string[] signedIn, Dictionary<string, PathState> states) =>
        InstalledFootprint.PlanDataRemoval(
            [new(AnaSid, @"C:\Users\ana"), new(BenSid, @"C:\Users\ben")],
            new HashSet<string>(signedIn, StringComparer.OrdinalIgnoreCase),
            path => states.TryGetValue(path, out var state) ? state : PathState.Missing);
}
