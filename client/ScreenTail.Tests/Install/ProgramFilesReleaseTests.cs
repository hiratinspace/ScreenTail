using ScreenTail.Core.Install;

namespace ScreenTail.Tests.Install;

/// <summary>
/// The uninstall step's wait for other programs to let go of the installed files (ST-112). The step runs
/// straight before Windows Installer deletes them, and a file another program still has open is one
/// Windows Installer leaves for a restart, ending the uninstall with 3010. The clock here moves only when
/// the wait sleeps (or a test moves it while answering), so every duration below is exact.
/// </summary>
public sealed class ProgramFilesReleaseTests
{
    private const long Self = 100;
    private const long Scanner = 200;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Confirm = TimeSpan.FromMilliseconds(250);
    private static readonly string[] Files = [@"C:\Program Files\ScreenTail\ScreenTail.Service.exe", @"C:\Program Files\ScreenTail\ScreenTail.UI.exe"];

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 1, 3, 20, 0, TimeSpan.Zero));
    private readonly List<TimeSpan> _sleeps = [];

    private ProgramFilesRelease.Outcome Wait(Func<string, IReadOnlyCollection<long>?> holders, TimeSpan? budget = null) =>
        ProgramFilesRelease.WaitForOthers(Files, holders, Self, _time, Sleep, budget ?? Budget, Poll, Confirm);

    private void Sleep(TimeSpan by)
    {
        _sleeps.Add(by);
        _time.Advance(by);
    }

    private TimeSpan Elapsed(DateTimeOffset since) => _time.GetUtcNow() - since;

    private static bool IsService(string file) => file.EndsWith("Service.exe", StringComparison.Ordinal);

    [Fact]
    public void NothingHeldIsLookedAtTwiceAndIsNoWait()
    {
        var outcome = Wait(_ => []);

        Assert.Equal(TimeSpan.Zero, outcome.Waited);
        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal([Confirm], _sleeps);
    }

    [Fact]
    public void AMonitorThatOpensTheFileJustAfterACleanLookIsSeenByTheSecond()
    {
        // The window the first version left open: it exited on the first clean look, so a monitor that
        // opened the executable a moment later was never seen and still had it when Windows Installer
        // came to delete it. Here it opens 100 ms after the first look and holds it for a second.
        var start = _time.GetUtcNow();
        var asked = new List<TimeSpan>();
        IReadOnlyCollection<long> Holders(string file)
        {
            var at = Elapsed(start);
            if (IsService(file))
            {
                asked.Add(at);
            }

            return IsService(file) && at >= TimeSpan.FromMilliseconds(100) && at < TimeSpan.FromMilliseconds(1_100) ? [Self, Scanner] : [Self];
        }

        var outcome = Wait(Holders);

        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal(TimeSpan.FromMilliseconds(250), asked[1]);
        // Held from the second look to 1100 ms, then two clean looks 250 ms apart.
        Assert.Equal(TimeSpan.FromMilliseconds(1_350), outcome.Waited);
    }

    [Fact]
    public void AskingIsNotWaiting()
    {
        // Asking Windows about every installed file takes tens of milliseconds on a real machine. When no
        // look finds anybody else, the step waited for nobody and says so.
        var outcome = Wait(_ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(3));
            return [Self];
        });

        Assert.Equal(TimeSpan.Zero, outcome.Waited);
        Assert.Equal(0, outcome.StillHeld);
    }

    [Fact]
    public void ThisProcessHoldingItsOwnImageIsNotWaitedFor()
    {
        // The step runs from ScreenTail.Service.exe, so that file always lists this process. Its hold ends
        // when it exits, before Windows Installer moves on; waiting for it would always spend the budget.
        var outcome = Wait(file => IsService(file) ? [Self] : []);

        Assert.Equal(TimeSpan.Zero, outcome.Waited);
        Assert.Equal(0, outcome.StillHeld);
    }

    [Fact]
    public void AnotherProgramReadingTheExecutableIsWaitedForUntilItLetsGo()
    {
        // What GitHub's runner monitor did on 2026-11-01: it had the 131 MB executable open for about four
        // seconds, and the step exited while it still had it.
        var start = _time.GetUtcNow();
        var outcome = Wait(file => IsService(file) && Elapsed(start) < TimeSpan.FromSeconds(4) ? [Self, Scanner] : [Self]);

        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal(TimeSpan.FromMilliseconds(4_250), outcome.Waited);
        Assert.All(_sleeps.SkipLast(1), s => Assert.Equal(Poll, s));
        Assert.Equal(Confirm, _sleeps[^1]);
    }

    [Fact]
    public void AFileOpenedAgainAfterBeingLetGoIsWaitedForAgain()
    {
        // Every file is asked on every look: a scanner that lets go of the service and then opens the UI,
        // or the service again, has not finished.
        var start = _time.GetUtcNow();
        IReadOnlyCollection<long> Holders(string file)
        {
            var at = Elapsed(start);
            return (IsService(file) && at < TimeSpan.FromMilliseconds(200)) || (!IsService(file) && at >= TimeSpan.FromMilliseconds(200) && at < TimeSpan.FromMilliseconds(500))
                ? [Scanner]
                : [];
        }

        var outcome = Wait(Holders);

        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal(TimeSpan.FromMilliseconds(750), outcome.Waited);
    }

    [Fact]
    public void AHoldThatNeverEndsSpendsTheBudgetAndIsCounted()
    {
        // Bounded: the step must finish inside WixQuietExec's minute, and a file left open is then
        // Windows Installer's to deal with, which it does by asking for a restart.
        var outcome = Wait(_ => [Scanner]);

        Assert.Equal(Budget, outcome.Waited);
        Assert.Equal(Files.Length, outcome.StillHeld);
        Assert.Equal(Budget, _sleeps.Aggregate(TimeSpan.Zero, (sum, s) => sum + s));
    }

    [Fact]
    public void AWaitThatRunsOutSaysWhichFilesWereStillHeldAtTheLastLook()
    {
        // What makes a wait that ran out diagnosable: the files held at the last look, in the order given,
        // not the ones held at some earlier look. Here the UI is let go halfway; only the service is left.
        var start = _time.GetUtcNow();
        var outcome = Wait(file => IsService(file) || Elapsed(start) < Budget / 2 ? [Scanner] : []);

        Assert.Equal([Files[0]], outcome.Held);
        Assert.Equal(1, outcome.StillHeld);
    }

    [Fact]
    public void NothingHeldAtTheEndNamesNothing()
    {
        Assert.Empty(Wait(_ => []).Held);
        Assert.Equal(0, default(ProgramFilesRelease.Outcome).StillHeld);
    }

    [Fact]
    public void FilesStillHeldAreNamedBelowTheInstallFolderOnly()
    {
        // Installed file names are ScreenTail's own; the folder they are in, and anything outside it, is not
        // named (INV-10). Compared without regard to case, with or without the folder's trailing separator,
        // and a sibling folder whose name merely starts the same is outside.
        var held = new[]
        {
            @"C:\Program Files\ScreenTail\ScreenTail.Service.exe",
            @"c:\program files\screentail\runtimes\win-x64\whisper.dll",
            @"C:\Program Files\ScreenTailOther\x.dll",
            @"C:\Users\someone\secret.txt",
        };

        string[] expected = ["ScreenTail.Service.exe", @"runtimes\win-x64\whisper.dll", "(a file outside the install folder)", "(a file outside the install folder)"];
        Assert.Equal(expected, ProgramFilesRelease.BelowInstallFolder(held, @"C:\Program Files\ScreenTail"));
        Assert.Equal(expected, ProgramFilesRelease.BelowInstallFolder(held, @"C:\Program Files\ScreenTail\"));
    }

    [Fact]
    public void TheLastSleepStopsAtTheBudget()
    {
        var outcome = Wait(_ => [Scanner], TimeSpan.FromMilliseconds(120));

        Assert.Equal(TimeSpan.FromMilliseconds(120), outcome.Waited);
        Assert.Equal([Poll, Poll, TimeSpan.FromMilliseconds(20)], _sleeps);
    }

    [Fact]
    public void TheSecondLookStaysInsideTheBudget()
    {
        var outcome = Wait(_ => [], TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal([TimeSpan.FromMilliseconds(100)], _sleeps);
    }

    [Fact]
    public void WithNoBudgetLeftItLooksOnceAndReturns()
    {
        // The step already used its minute's share stopping copies and removing data: say what is held,
        // and get out of Windows Installer's way.
        var outcome = Wait(_ => [Scanner], TimeSpan.Zero);

        Assert.Equal(Files.Length, outcome.StillHeld);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void AFileThatCannotBeAskedAboutDoesNotHoldTheWait()
    {
        // Gone already, or refused: either way nothing here can make it free sooner.
        var outcome = Wait(_ => null);

        Assert.Equal(TimeSpan.Zero, outcome.Waited);
        Assert.Equal(0, outcome.StillHeld);
    }

    [Fact]
    public void TheWaitGetsWhatIsLeftOfTheStepsMinute()
    {
        // WixQuietExec waits a minute for the step, then lets Windows Installer carry on with the step's
        // executable still mapped, which is 3010 too. Stopping copies (up to ten seconds each) and removing
        // every profile's data come first; the wait gets what is left before the step's deadline.
        Assert.Equal(ProgramFilesRelease.Budget, ProgramFilesRelease.BudgetLeft(TimeSpan.Zero));
        Assert.Equal(ProgramFilesRelease.Budget, ProgramFilesRelease.BudgetLeft(ProgramFilesRelease.StepDeadline - ProgramFilesRelease.Budget));
        Assert.Equal(TimeSpan.FromSeconds(5), ProgramFilesRelease.BudgetLeft(ProgramFilesRelease.StepDeadline - TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.Zero, ProgramFilesRelease.BudgetLeft(ProgramFilesRelease.StepDeadline));
        Assert.Equal(TimeSpan.Zero, ProgramFilesRelease.BudgetLeft(TimeSpan.FromMinutes(2)));

        Assert.True(ProgramFilesRelease.StepDeadline + TimeSpan.FromSeconds(10) <= TimeSpan.FromMinutes(1), "the deadline leaves room inside WixQuietExec's minute");
        Assert.InRange(ProgramFilesRelease.Budget, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        Assert.True(ProgramFilesRelease.PollEvery < ProgramFilesRelease.ConfirmAfter);
    }
}
