namespace ScreenTail.Core.Install;

/// <summary>
/// The uninstall step's last act (ST-112, ADR-0007): wait, for a bounded time, until no other program
/// has ScreenTail's installed files open, so Windows Installer can delete them the moment the step exits.
///
/// Windows Installer removes the files straight after this step, which runs from the installed service's
/// own executable. A program that has one of those files open without sharing deletion makes Windows
/// Installer schedule that file for deletion at the next restart and end with 3010, "restart required",
/// leaving the install folder behind. Programs that open other programs' executables are common: virus
/// scanners and endpoint agents read the file of a program that is running. On GitHub's hosted runner its
/// own monitor (provjobd) opens a ScreenTail executable while the process runs, at a moment of its own
/// choosing, and keeps it open for about four seconds, past the process's exit, at times without sharing
/// deletion. When it had ScreenTail.Service.exe open as the step exited, the uninstall ended with 3010
/// (2026-11-01).
///
/// <b>Two clean looks.</b> A monitor can open the file just after a look that found nothing, so one clean
/// look is not enough: the step exits only after two in a row, <see cref="ConfirmAfter"/> apart. A hold
/// that starts after the second, in the moment before the process exits, is the one case this cannot see.
///
/// <b>Bounded as a whole.</b> WixQuietExec waits a minute for the step and then lets Windows Installer
/// carry on with the step still running and its executable still mapped, which ends in 3010 as surely as
/// a scanner does. Stopping running copies and removing every profile's data come first and take what
/// they take, so the wait gets what is left of <see cref="StepDeadline"/>, never more than
/// <see cref="Budget"/> (<see cref="BudgetLeft"/>).
///
/// What counts as "another program" is any process but this one: this process holds its own image, which
/// goes when it exits, before Windows Installer moves on. ScreenTail's own copies were stopped earlier in
/// the step; one that has not gone yet is waited for like anything else. A hold that shares deletion is
/// waited for too: the file it keeps is deleted only when it lets go, and the folder cannot go before.
///
/// Every file is asked again on every round, because a scanner can open a file again after letting go of
/// it. A file that cannot be asked about (gone, or refused) does not hold the wait.
/// </summary>
public static class ProgramFilesRelease
{
    /// <summary>
    /// Long enough for a scanner to finish reading a 131 MB executable on a slow machine several times
    /// over (the runner's monitor took about four seconds).
    /// </summary>
    public static TimeSpan Budget { get; } = TimeSpan.FromSeconds(15);

    /// <summary>How long between rounds while a file is held.</summary>
    public static TimeSpan PollEvery { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long after a clean look the step looks again before it trusts it.</summary>
    public static TimeSpan ConfirmAfter { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// When the whole step must be done by, from its start: WixQuietExec's minute, less a quarter of it for
    /// writing the report and exiting.
    /// </summary>
    public static TimeSpan StepDeadline { get; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long the step waited, and how many files another program still had open when it stopped. No
    /// wait at all when no look found a file held: asking, and looking a second time, is not waiting for
    /// anybody.
    /// </summary>
    public readonly record struct Outcome(TimeSpan Waited, int StillHeld);

    /// <summary>
    /// The wait's budget once the step has already used <paramref name="used"/>: what is left of
    /// <see cref="StepDeadline"/>, at most <see cref="Budget"/>, never below zero. With nothing left the
    /// wait looks once and returns.
    /// </summary>
    public static TimeSpan BudgetLeft(TimeSpan used)
    {
        var left = StepDeadline - used;
        if (left <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return left < Budget ? left : Budget;
    }

    /// <param name="files">The installed files Windows Installer is about to remove.</param>
    /// <param name="holders">The ids of the processes that have a file open; null when the file cannot be asked about.</param>
    /// <param name="self">This process's id, whose own hold ends when it exits.</param>
    /// <param name="time">The clock the budget is measured on.</param>
    /// <param name="sleep">Waits between looks.</param>
    /// <param name="budget">The longest the step waits, the second look included.</param>
    /// <param name="pollEvery">How long between looks while a file is held.</param>
    /// <param name="confirmAfter">How long after a clean look the next one is.</param>
    public static Outcome WaitForOthers(
        IReadOnlyCollection<string> files,
        Func<string, IReadOnlyCollection<long>?> holders,
        long self,
        TimeProvider time,
        Action<TimeSpan> sleep,
        TimeSpan budget,
        TimeSpan pollEvery,
        TimeSpan confirmAfter)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(holders);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(sleep);

        var start = time.GetTimestamp();
        var everHeld = false;
        var cleanLooks = 0;
        while (true)
        {
            var held = files.Count(file => holders(file)?.Any(id => id != self) == true);
            var waited = time.GetElapsedTime(start);
            everHeld |= held > 0;
            cleanLooks = held == 0 ? cleanLooks + 1 : 0;
            if (cleanLooks >= 2 || waited >= budget)
            {
                return new Outcome(everHeld ? waited : TimeSpan.Zero, held);
            }

            var pause = held == 0 ? confirmAfter : pollEvery;
            var left = budget - waited;
            sleep(left < pause ? left : pause);
        }
    }
}
