using System.Runtime.Versioning;
using ScreenTail.Service.Install;
using Outcome = ScreenTail.Service.Install.SecretShredder.Outcome;
using Result = ScreenTail.Service.Install.SecretShredder.Result;

namespace ScreenTail.Tests.Windows.Install;

/// <summary>
/// How the uninstall counts a signed-out technician's folder (ST-112): what its log says has to be what
/// happened. A folder counts as having had its key and credentials removed only when it lost a file; a
/// folder the shredder would not open is counted once, as such; and a failure for one profile is
/// counted without stopping the next.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UninstallTallyTests
{
    private static readonly Result Absent = new(Outcome.Absent, null);
    private static readonly Result Removed = new(Outcome.Removed, null);

    [Fact]
    public void AFolderWithNothingInItIsNotCounted()
    {
        var tally = new UninstallCleanup.Tally();

        UninstallCleanup.Record([Absent, Absent, Absent, Absent], tally);

        Assert.Equal(0, tally.Shredded + tally.SecretsRemoved + tally.SecretsInDoubt + tally.FoldersUnopened);
    }

    [Fact]
    public void AFolderThatLostAFileIsCountedWithWhatWasLeft()
    {
        var tally = new UninstallCleanup.Tally();

        UninstallCleanup.Record([Removed, Removed, new(Outcome.LeftInDoubt, "link-or-directory"), Absent], tally);

        Assert.Equal(1, tally.Shredded);
        Assert.Equal(2, tally.SecretsRemoved);
        Assert.Equal(1, tally.SecretsInDoubt);
        Assert.Equal(0, tally.FoldersUnopened);
        Assert.Equal(1, tally.DoubtReasons["link-or-directory"]);
    }

    [Fact]
    public void AFolderThatLostNothingIsNotCountedAsShredded()
    {
        var tally = new UninstallCleanup.Tally();

        UninstallCleanup.Record([new(Outcome.LeftInDoubt, "hard-linked"), new(Outcome.LeftInDoubt, "hard-linked"), Absent, Absent], tally);

        Assert.Equal(0, tally.Shredded);
        Assert.Equal(2, tally.SecretsInDoubt);
        Assert.Equal(2, tally.DoubtReasons["hard-linked"]);
    }

    [Theory]
    [InlineData("dir-not-a-plain-directory")]
    [InlineData("root-open:5")]
    [InlineData("dir-open:c0000022")]
    public void AFolderThatWasNotOpenedIsCountedOnceAsSuch(string reason)
    {
        var tally = new UninstallCleanup.Tally();
        var refused = new Result(Outcome.LeftInDoubt, reason);

        UninstallCleanup.Record([refused, refused, refused, refused], tally);

        Assert.Equal(1, tally.FoldersUnopened);
        Assert.Equal(0, tally.Shredded);
        Assert.Equal(0, tally.SecretsInDoubt);
        Assert.Equal(1, tally.DoubtReasons[reason]);
    }

    [Fact]
    public void AFailureForOneProfileIsCountedAndTheNextIsStillHandled()
    {
        var tally = new UninstallCleanup.Tally();

        UninstallCleanup.Shred("first", tally, _ => throw new IOException("the disk went away"));
        UninstallCleanup.Shred("second", tally, _ => [Removed, Removed, Absent, Absent]);

        Assert.Equal(1, tally.FoldersUnopened);
        Assert.Equal(1, tally.DoubtReasons["exception"]);
        Assert.Equal(1, tally.Shredded);
        Assert.Equal(2, tally.SecretsRemoved);
    }
}
