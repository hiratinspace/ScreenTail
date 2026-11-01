using System.Runtime.Versioning;
using ScreenTail.Core.Install;

namespace ScreenTail.Service.Install;

/// <summary>
/// The installer's step when an upgrade removes this version (ST-112, ADR-0007).
///
/// A major upgrade removes the old version completely before it installs the new one, and that removal is
/// an uninstall: Windows Installer deletes the installed files at its RemoveFiles step. It does not run
/// the uninstall step (<see cref="UninstallCleanup"/>), which would erase every technician's data, so on
/// an upgrade nothing used to wait for other programs to let go of the files first. Restart Manager ends
/// the running service about a second before those files are deleted (the upgrade logs of 2026-11-01),
/// and a scanner or endpoint agent that reads a program's file while it runs can still have
/// ScreenTail.Service.exe open, without sharing deletion, a few seconds after it exits: the hosted runner's
/// monitor does exactly that. Windows Installer then leaves the file for a restart and the upgrade ends
/// with 3010, with a file operation on the path the new executable goes to still waiting for that restart.
///
/// So the removal of an upgraded version runs this first, from the version being removed, which knows its
/// own files: it stops any copy still running from the install folder, as an upgrade always has (only
/// earlier), and then waits, as the uninstall step's last act does, until no other program has an
/// installed file open (<see cref="ProgramFilesRelease"/>: two clean looks, bounded inside WixQuietExec's
/// minute). Nothing else: it reads no profile, removes no data and no Run value, and runs no capture code.
/// It takes no argument but its flag, and the folder it looks in is the one its own executable is in,
/// which the package only ever puts under Program Files; the files are listed without following a link.
///
/// What it prints is counts, never a path or a name (INV-10); the installer writes it into its log.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UpgradeRelease
{
    /// <summary>The start of each line it prints, which is what to search the installer's log for.</summary>
    public const string Prefix = "ScreenTail upgrade:";

    /// <returns>0 when nothing was left open, 1 when something was, 2 when it was not allowed to run.</returns>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // WixQuietExec gives the whole step a minute; the wait at the end gets what is left of it.
        var started = TimeProvider.System.GetTimestamp();

        // For the installer, which runs it as the system account. Ending another user's copy needs that
        // account anyway; an ordinary user who found the flag gets a sentence and nothing done.
        if (!UninstallCleanup.RunByTheInstaller())
        {
            output.WriteLine("This step is run by the ScreenTail installer as an administrator. Nothing was done.");
            return 2;
        }

        return Run(output, AppContext.BaseDirectory, started);
    }

    /// <summary>The step itself, for the installed folder or, in a test, any other.</summary>
    internal static int Run(TextWriter output, string installDirectory, long started)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(installDirectory);

        var stopped = UninstallCleanup.StopInstalledCopies(installDirectory);

        // Last: Windows Installer deletes the installed files the moment this process exits.
        var release = UninstallCleanup.ReleaseInstalledFiles(installDirectory, started);

        output.WriteLine($"{Prefix} {stopped} running copies stopped.");
        output.WriteLine(
            $"{Prefix} waited {(long)release.Waited.TotalMilliseconds} ms for other programs to close the installed files; "
            + $"{release.StillHeld} still open.");
        return release.StillHeld == 0 ? 0 : 1;
    }
}
