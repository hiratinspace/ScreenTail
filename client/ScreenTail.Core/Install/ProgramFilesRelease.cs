namespace ScreenTail.Core.Install;

/// <summary>
/// The uninstall step's last act (ST-112, ADR-0007): wait, for a bounded time, until no other program
/// has ScreenTail's installed files open, so Windows Installer can delete them the moment the step exits.
///
/// Windows Installer removes the files straight after this step, which runs from the installed service's
/// own executable. A program that has one of those files open without sharing deletion makes Windows
/// Installer schedule that file for deletion at the next restart and end with 3010, "restart required",
/// leaving the install folder behind. Programs that open other programs' executables are common: virus
/// scanners and endpoint agents read a program's file when it starts, and on GitHub's hosted runner its own
/// monitor (provjobd) read ScreenTail.Service.exe, 131 MB, for about a second while the service ran. When
/// that read was still going as the step exited, the uninstall ended with 3010 (2026-11-01).
///
/// What counts as "another program" is any process but this one: this process holds its own image, which
/// goes when it exits, before Windows Installer moves on. ScreenTail's own copies were stopped earlier in
/// the step; one that has not gone yet is waited for like anything else.
///
/// Every file is asked again on every round, because a scanner can open a file again after letting go of
/// it: the wait ends on a round in which nothing is held, or when the budget is spent. A file that cannot
/// be asked about (gone, or refused) does not hold the wait.
/// </summary>
public static class ProgramFilesRelease
{
    /// <summary>
    /// Long enough for a scanner to finish reading a 131 MB executable on a slow machine many times over;
    /// short enough that the whole step stays well inside the minute WixQuietExec gives it.
    /// </summary>
    public static TimeSpan Budget { get; } = TimeSpan.FromSeconds(15);

    public static TimeSpan PollEvery { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the step waited, and how many files another program still had open when it stopped.</summary>
    public readonly record struct Outcome(TimeSpan Waited, int StillHeld);

    /// <param name="files">The installed files Windows Installer is about to remove.</param>
    /// <param name="holders">The ids of the processes that have a file open; null when the file cannot be asked about.</param>
    /// <param name="self">This process's id, whose own hold ends when it exits.</param>
    /// <param name="time">The clock the budget is measured on.</param>
    /// <param name="sleep">Waits between rounds.</param>
    /// <param name="budget">The longest the step waits.</param>
    /// <param name="pollEvery">How long between rounds.</param>
    public static Outcome WaitForOthers(
        IReadOnlyCollection<string> files,
        Func<string, IReadOnlyCollection<long>?> holders,
        long self,
        TimeProvider time,
        Action<TimeSpan> sleep,
        TimeSpan budget,
        TimeSpan pollEvery)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(holders);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(sleep);

        var start = time.GetTimestamp();
        while (true)
        {
            var held = files.Count(file => holders(file)?.Any(id => id != self) == true);
            var waited = time.GetElapsedTime(start);
            if (held == 0 || waited >= budget)
            {
                return new Outcome(waited, held);
            }

            var left = budget - waited;
            sleep(left < pollEvery ? left : pollEvery);
        }
    }
}
