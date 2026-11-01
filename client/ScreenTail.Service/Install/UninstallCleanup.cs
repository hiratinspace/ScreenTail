using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Core.Install;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Service.Install;

/// <summary>
/// The uninstaller's own step (ST-112, ADR-0007): what an installed ScreenTail made outside its folder,
/// for every user, cleared once for the whole machine.
///
/// Windows Installer removes what it installed: the files, the machine Run value, the shortcut. It does
/// not know about what the product made afterwards, per user: each technician's data folder (the
/// encrypted store, its key, the device's refresh token, the speech models, settings, crash reports) and
/// the Run value the UI writes for "Start UI at login". The installer runs this as the system account,
/// after it has closed every running copy and before it removes the files, and never during an upgrade.
///
/// <see cref="InstalledFootprint.PlanDataRemoval"/> decides, per profile:
/// <list type="bullet">
/// <item>A user who is signed in: the whole folder is deleted under that user's own token, the store's
/// key and the device's credentials first (Delete everything's order), so it has no more rights than the
/// folder's owner whatever the folder turns out to point at. If that fails, the user is treated as below.</item>
/// <item>A user who is not signed in: the system account never deletes a folder. <see cref="SecretShredder"/>
/// removes only the key and the credentials, through handles that refuse links, and the store that stays
/// cannot be read without its key.</item>
/// <item>A link anywhere between the profile and the folder, or a profile path that cannot be read
/// plainly: nothing is touched.</item>
/// </list>
/// Everything left is counted in what this prints, which the installer writes into its log. What it
/// prints is counts, never a path or a name (INV-10). A failure for one user is counted and the next user
/// is handled; the Run values are removed whatever happened before.
///
/// Last of all it waits until no other program has an installed file open, two looks in a row, because
/// Windows Installer deletes them as soon as this exits and leaves one still open for a restart. The wait
/// gets what is left of the step's own deadline, at most <see cref="ProgramFilesRelease.Budget"/>, so the
/// step as a whole ends inside WixQuietExec's minute (<see cref="ProgramFilesRelease"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UninstallCleanup
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private static readonly string[] ProcessNames =
    [
        Path.GetFileNameWithoutExtension(InstalledFootprint.ServiceExecutable),
        Path.GetFileNameWithoutExtension(InstalledFootprint.UiExecutable),
    ];

    /// <summary>What happened to the data, counted across every profile.</summary>
    internal sealed class Tally
    {
        public int Deleted { get; set; }

        public int PartlyDeleted { get; set; }

        public int Shredded { get; set; }

        public int SecretsRemoved { get; set; }

        public int SecretsInDoubt { get; set; }

        /// <summary>Signed-out data folders the shredder would not or could not open, left as they are.</summary>
        public int FoldersUnopened { get; set; }

        public int Linked { get; set; }

        public int Unreadable { get; set; }

        /// <summary>Why files were left in doubt, by reason token, counted. No path or name (INV-10).</summary>
        public Dictionary<string, int> DoubtReasons { get; } = new(StringComparer.Ordinal);
    }

    /// <returns>0 when nothing was left in doubt, 1 when something was, 2 when it was not allowed to run.</returns>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // WixQuietExec gives the whole step a minute; the wait at the end gets what is left of it.
        var started = TimeProvider.System.GetTimestamp();

        // For the uninstaller, which runs it as the system account: holding another user's token needs
        // that account's privilege anyway. An ordinary user who found the flag gets a sentence and
        // nothing removed; "delete everything" for one person is the Settings button, which asks first.
        if (!RunByTheInstaller())
        {
            output.WriteLine("This step is run by the ScreenTail uninstaller as an administrator. Nothing was removed.");
            return 2;
        }

        var installDirectory = AppContext.BaseDirectory;
        var stopped = StopInstalledCopies(installDirectory);

        var tally = new Tally();
        try
        {
            RemoveData(tally);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception or InvalidOperationException)
        {
            // Counted as in doubt rather than thrown: the Run values below are removed either way.
            tally.SecretsInDoubt++;
        }

        var (valuesRemoved, valuesLeft) = RemoveRunValues(installDirectory);

        // Last, so nothing this step does afterwards can open a file again: Windows Installer deletes the
        // installed files the moment this process exits, and one another program still has open is left
        // for a restart, which ends the uninstall with 3010 (ProgramFilesRelease says who does that).
        var release = ReleaseInstalledFiles(installDirectory, started);

        output.WriteLine($"ScreenTail uninstall: {stopped} running copies stopped.");
        output.WriteLine(
            $"ScreenTail uninstall: signed in: {tally.Deleted} data folders deleted with their owner's rights, "
            + $"{tally.PartlyDeleted} only partly (key and credentials gone, the rest left).");
        output.WriteLine(
            $"ScreenTail uninstall: not signed in: {tally.Shredded} data folders had their key and credentials removed "
            + $"({tally.SecretsRemoved} files), so the store left in them cannot be read; {tally.SecretsInDoubt} files left in doubt; "
            + $"{tally.FoldersUnopened} data folders could not be opened safely and were left as they are.");
        output.WriteLine(
            $"ScreenTail uninstall: left alone: {tally.Linked} reached through a link, {tally.Unreadable} profiles whose path could not be read plainly.");
        if (tally.DoubtReasons.Count > 0)
        {
            var reasons = string.Join(", ", tally.DoubtReasons.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}"));
            output.WriteLine($"ScreenTail uninstall: left in doubt, by reason (files, or folders not opened): {reasons}.");
        }

        output.WriteLine($"ScreenTail uninstall: {valuesRemoved} Run values removed, {valuesLeft} could not be removed.");
        output.WriteLine(
            $"ScreenTail uninstall: waited {(long)release.Waited.TotalMilliseconds} ms for other programs to close the installed files; "
            + $"{release.StillHeld} still open.");

        return tally.PartlyDeleted + tally.SecretsInDoubt + tally.FoldersUnopened + tally.Linked + tally.Unreadable + valuesLeft == 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether this process is the installer's: the system account, or an administrator. Holding another
    /// user's token needs the system account's privilege anyway; the check is what gives an ordinary user
    /// who found a flag a sentence instead of a half-done step.
    /// </summary>
    internal static bool RunByTheInstaller()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// The installer's steps' last act (<see cref="ProgramFilesRelease"/>): wait until no process but this
    /// one has a file in <paramref name="installDirectory"/> open, two clean looks apart, for what is left
    /// of the step's deadline counted from <paramref name="started"/>, the step's own first timestamp.
    /// Windows Installer removes the files the moment the step exits.
    /// </summary>
    internal static ProgramFilesRelease.Outcome ReleaseInstalledFiles(string installDirectory, long started) =>
        ProgramFilesRelease.WaitForOthers(
            InstalledFiles(installDirectory),
            FileHolders.Of,
            Environment.ProcessId,
            TimeProvider.System,
            Thread.Sleep,
            ProgramFilesRelease.BudgetLeft(TimeProvider.System.GetElapsedTime(started)),
            ProgramFilesRelease.PollEvery,
            ProgramFilesRelease.ConfirmAfter);

    /// <summary>
    /// Every service and UI started from this installation, in every session. The installer has already
    /// closed them; this is for one that started since, and it is what lets a store be deleted, since an
    /// open database file cannot be.
    /// </summary>
    internal static int StopInstalledCopies(string installDirectory)
    {
        var stopped = 0;
        foreach (var process in InstalledCopies(installDirectory))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    _ = process.WaitForExit(TimeSpan.FromSeconds(10));
                    stopped++;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Already gone, or not ours to stop. Its files are then in use, and the installer
                    // deals with that the way it deals with any file in use.
                }
            }
        }

        return stopped;
    }

    /// <summary>
    /// Gives the copies running from this installation up to <paramref name="grace"/>, in all, to end on
    /// their own, and says how many did. For an upgrade, which keeps the technicians' data: Restart Manager
    /// has already asked them to close, and one still finishing its own shutdown is let finish rather than
    /// ended halfway. What is still running after it is <see cref="StopInstalledCopies"/>'s.
    /// </summary>
    internal static int AwaitInstalledCopies(string installDirectory, TimeSpan grace)
    {
        var start = TimeProvider.System.GetTimestamp();
        var ended = 0;
        foreach (var process in InstalledCopies(installDirectory))
        {
            using (process)
            {
                var left = grace - TimeProvider.System.GetElapsedTime(start);
                try
                {
                    if (process.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                    {
                        ended++;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Cannot be waited for; stopping it comes next either way.
                }
            }
        }

        return ended;
    }

    /// <summary>
    /// The services and UIs running from this installation, in every session, other than this process,
    /// by the kernel's record of each one's image, not the process's own (ProcessImage says why). A copy
    /// of ScreenTail running from anywhere else is not this installation's. The caller disposes them.
    /// </summary>
    private static List<Process> InstalledCopies(string installDirectory)
    {
        var copies = new List<Process>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using var image = process.Id == Environment.ProcessId ? null : ProcessImage.Of((uint)process.Id);
                if (image is not null && InstalledFootprint.IsInside(image.Path, installDirectory))
                {
                    copies.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
        }

        return copies;
    }

    /// <summary>
    /// The files Windows Installer is about to remove: everything in the install folder, without
    /// following a link out of it. Empty when the folder cannot be listed, which makes the wait a no-op.
    /// </summary>
    private static List<string> InstalledFiles(string installDirectory)
    {
        try
        {
            return [.. Directory.EnumerateFiles(installDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            })];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static void RemoveData(Tally tally)
    {
        var owners = SignedInUsers();
        try
        {
            var plan = InstalledFootprint.PlanDataRemoval(Profiles(), owners.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), Probe);
            foreach (var decision in plan)
            {
                switch (decision.Action)
                {
                    case DataFolderAction.DeleteAsOwner:
                        var owned = OwnerDelete.Failed;
                        try
                        {
                            owned = WindowsIdentity.RunImpersonated(owners[decision.Sid].AccessToken, () => DeleteAsOwner(decision.Profile!, decision.Folder!));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception or InvalidOperationException)
                        {
                            owned = OwnerDelete.Failed;
                        }

                        if (owned == OwnerDelete.Deleted)
                        {
                            tally.Deleted++;
                        }
                        else if (owned == OwnerDelete.SecretsOnly)
                        {
                            tally.PartlyDeleted++;
                        }
                        else
                        {
                            // The owner's own rights were not enough, or something failed under them: the
                            // system-side removal of the key and credentials is the fallback.
                            Shred(decision.Profile!, tally, SecretShredder.Shred);
                        }

                        break;
                    case DataFolderAction.ShredSecrets:
                        Shred(decision.Profile!, tally, SecretShredder.Shred);
                        break;
                    case DataFolderAction.LeaveLinked:
                        tally.Linked++;
                        break;
                    case DataFolderAction.LeaveUnreadablePath:
                        tally.Unreadable++;
                        break;
                    case DataFolderAction.Absent:
                    default:
                        break;
                }
            }
        }
        finally
        {
            foreach (var owner in owners.Values)
            {
                owner.Dispose();
            }
        }
    }

    /// <summary>
    /// One signed-out profile. A failure here is counted for this profile and the next one is still
    /// handled.
    /// </summary>
    internal static void Shred(string profile, Tally tally, Func<string, IReadOnlyList<SecretShredder.Result>> shred)
    {
        IReadOnlyList<SecretShredder.Result> results;
        try
        {
            results = shred(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or Win32Exception or InvalidOperationException or ArgumentException or ExternalException)
        {
            tally.FoldersUnopened++;
            AddReason(tally, "exception");
            return;
        }

        Record(results, tally);
    }

    /// <summary>
    /// What one profile's results add up to. A folder counts as shredded only when it lost at least one
    /// file; one the shredder refused to open (a link on the way, or a folder it could not open) is
    /// counted as such, once, not as four files in doubt.
    /// </summary>
    internal static void Record(IReadOnlyList<SecretShredder.Result> results, Tally tally)
    {
        if (results.All(r => r.Outcome == SecretShredder.Outcome.Absent))
        {
            return;
        }

        var removed = results.Count(r => r.Outcome == SecretShredder.Outcome.Removed);
        var doubt = results.Where(r => r.Outcome == SecretShredder.Outcome.LeftInDoubt).ToList();
        if (removed == 0 && doubt.Count == results.Count && doubt.All(r => IsFolderLevel(r.Reason)))
        {
            tally.FoldersUnopened++;
            AddReason(tally, doubt[0].Reason!);
            return;
        }

        if (removed > 0)
        {
            tally.Shredded++;
        }

        tally.SecretsRemoved += removed;
        tally.SecretsInDoubt += doubt.Count;
        foreach (var reason in doubt.Where(r => r.Reason is not null))
        {
            AddReason(tally, reason.Reason!);
        }
    }

    private static bool IsFolderLevel(string? reason) =>
        reason is not null && (reason.StartsWith("root-", StringComparison.Ordinal) || reason.StartsWith("dir-", StringComparison.Ordinal));

    private static void AddReason(Tally tally, string reason) =>
        tally.DoubtReasons[reason] = tally.DoubtReasons.GetValueOrDefault(reason) + 1;

    private enum OwnerDelete
    {
        Deleted,
        SecretsOnly,
        Failed,
    }

    /// <summary>
    /// Runs as the folder's owner, so it can do nothing its owner could not. The link check is asked again
    /// here, under the owner's token. The key and the credentials go first, in Delete everything's order,
    /// so a folder that cannot be removed in full is at least unreadable; then the folder, with .NET's
    /// recursive delete, which removes a link it meets inside rather than descending into it.
    /// </summary>
    private static OwnerDelete DeleteAsOwner(string profile, string folder)
    {
        if (InstalledFootprint.BelowProfile(profile).Select(Probe).Contains(PathState.Link))
        {
            return OwnerDelete.Failed;
        }

        foreach (var secret in InstalledFootprint.Secrets)
        {
            try
            {
                File.Delete(Path.Combine(folder, secret));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OwnerDelete.Failed;
            }
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The secrets are gone; what is left is reported as partly deleted.
        }

        return Directory.Exists(folder) ? OwnerDelete.SecretsOnly : OwnerDelete.Deleted;
    }

    /// <summary>What a folder is, without following it.</summary>
    private static PathState Probe(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            // A file where a folder should be is left alone along with a link: neither is ours to clear.
            return attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory)
                ? PathState.Link
                : PathState.Folder;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell, so it is treated as the case that is left alone.
            return PathState.Link;
        }
    }

    /// <summary>Every profile Windows lists on this machine: its owner and its folder.</summary>
    private static List<KeyValuePair<string, string?>> Profiles()
    {
        var profiles = new List<KeyValuePair<string, string?>>();
        using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
        if (list is null)
        {
            return profiles;
        }

        foreach (var sid in list.GetSubKeyNames())
        {
            using var entry = list.OpenSubKey(sid);

            // REG_EXPAND_SZ, expanded by GetValue. The plan leaves anything that is still not a plain
            // local folder after that.
            profiles.Add(new(sid, entry?.GetValue("ProfileImagePath") as string));
        }

        return profiles;
    }

    /// <summary>
    /// The identity of every user with a session on this machine, keyed by their security identifier.
    /// Asking for a session's token needs the system account's privilege; for anybody else this is
    /// empty, and the plan then deletes nothing.
    /// </summary>
    private static Dictionary<string, WindowsIdentity> SignedInUsers()
    {
        var users = new Dictionary<string, WindowsIdentity>(StringComparer.OrdinalIgnoreCase);
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out var count))
        {
            return users;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(sessions + (i * size));
                if (!WTSQueryUserToken(session.SessionId, out var token))
                {
                    continue;
                }

                using (token)
                {
                    var identity = new WindowsIdentity(token.DangerousGetHandle());
                    if (identity.User?.Value is { } sid && !users.ContainsKey(sid))
                    {
                        users[sid] = identity;
                    }
                    else
                    {
                        identity.Dispose();
                    }
                }
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }

        return users;
    }

    /// <summary>
    /// The Run values that start something from this installation, in every signed-in user's registry.
    /// A user who is not signed in has their registry in a file in their profile, which this does not
    /// open: a value left there names a program that no longer exists, and Windows passes over it.
    /// </summary>
    private static (int Removed, int Left) RemoveRunValues(string installDirectory)
    {
        var removed = 0;
        var left = 0;
        foreach (var user in Registry.Users.GetSubKeyNames())
        {
            if (user.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var run = Registry.Users.OpenSubKey($@"{user}\{InstalledFootprint.RunKey}", writable: true);
                if (run is null)
                {
                    continue;
                }

                var values = run.GetValueNames()
                    .Select(name => new KeyValuePair<string, string?>(name, run.GetValue(name) as string));
                foreach (var name in InstalledFootprint.RunValuesToRemove(values, installDirectory))
                {
                    try
                    {
                        run.DeleteValue(name, throwOnMissingValue: false);
                        removed++;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        left++;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                left++;
            }
        }

        return (removed, left);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(int sessionId, out SafeAccessTokenHandle token);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memory);
}
