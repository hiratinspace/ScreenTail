using ScreenTail.Core.Install;

namespace ScreenTail.Tests.Install;

/// <summary>
/// The uninstall step's wait for other programs to let go of the installed files (ST-112). The step runs
/// straight before Windows Installer deletes them, and a file another program still has open is one
/// Windows Installer leaves for a restart, ending the uninstall with 3010. The clock here moves only when
/// the wait sleeps, so every duration below is exact.
/// </summary>
public sealed class ProgramFilesReleaseTests
{
    private const long Self = 100;
    private const long Scanner = 200;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly string[] Files = [@"C:\Program Files\ScreenTail\ScreenTail.Service.exe", @"C:\Program Files\ScreenTail\ScreenTail.UI.exe"];

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 1, 3, 20, 0, TimeSpan.Zero));
    private readonly List<TimeSpan> _sleeps = [];

    private ProgramFilesRelease.Outcome Wait(Func<string, IReadOnlyCollection<long>?> holders) =>
        ProgramFilesRelease.WaitForOthers(Files, holders, Self, _time, Sleep, Budget, Poll);

    private void Sleep(TimeSpan by)
    {
        _sleeps.Add(by);
        _time.Advance(by);
    }

    private TimeSpan Elapsed(DateTimeOffset since) => _time.GetUtcNow() - since;

    [Fact]
    public void NothingHeldMeansNoWaitAtAll()
    {
        var outcome = Wait(_ => []);

        Assert.Equal(TimeSpan.Zero, outcome.Waited);
        Assert.Equal(0, outcome.StillHeld);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void AskingIsNotWaiting()
    {
        // Asking Windows about every installed file takes tens of milliseconds on a real machine. When the
        // first answer is that nobody else has them, the step waited for nobody and says so.
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
        var outcome = Wait(file => file.EndsWith("Service.exe", StringComparison.Ordinal) ? [Self] : []);

        Assert.Equal(TimeSpan.Zero, outcome.Waited);
        Assert.Equal(0, outcome.StillHeld);
    }

    [Fact]
    public void AnotherProgramReadingTheExecutableIsWaitedForUntilItLetsGo()
    {
        // What GitHub's runner monitor did on 2026-11-01: it read the 131 MB executable for about a
        // second, and the step exited while it still had it open.
        var start = _time.GetUtcNow();
        var outcome = Wait(file => file.EndsWith("Service.exe", StringComparison.Ordinal) && Elapsed(start) < TimeSpan.FromMilliseconds(1_000) ? [Self, Scanner] : [Self]);

        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), outcome.Waited);
        Assert.All(_sleeps, s => Assert.Equal(Poll, s));
    }

    [Fact]
    public void AFileOpenedAgainAfterBeingLetGoIsWaitedForAgain()
    {
        // Every file is asked on every round: a scanner that lets go of the service and then opens the UI,
        // or the service again, has not finished.
        var start = _time.GetUtcNow();
        IReadOnlyCollection<long> Holders(string file)
        {
            var at = Elapsed(start);
            var service = file.EndsWith("Service.exe", StringComparison.Ordinal);
            return (service && at < TimeSpan.FromMilliseconds(200)) || (!service && at >= TimeSpan.FromMilliseconds(200) && at < TimeSpan.FromMilliseconds(500))
                ? [Scanner]
                : [];
        }

        var outcome = Wait(Holders);

        Assert.Equal(0, outcome.StillHeld);
        Assert.Equal(TimeSpan.FromMilliseconds(500), outcome.Waited);
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
    public void TheLastSleepStopsAtTheBudget()
    {
        var outcome = ProgramFilesRelease.WaitForOthers(Files, _ => [Scanner], Self, _time, Sleep, TimeSpan.FromMilliseconds(120), Poll);

        Assert.Equal(TimeSpan.FromMilliseconds(120), outcome.Waited);
        Assert.Equal([Poll, Poll, TimeSpan.FromMilliseconds(20)], _sleeps);
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
    public void TheBudgetFitsInsideWixQuietExecsMinute()
    {
        // WixQuietExec waits a minute for the step, which also stops running copies (up to ten seconds
        // each) and removes every profile's data before this.
        Assert.InRange(ProgramFilesRelease.Budget, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        Assert.True(ProgramFilesRelease.PollEvery < TimeSpan.FromMilliseconds(250));
    }
}
