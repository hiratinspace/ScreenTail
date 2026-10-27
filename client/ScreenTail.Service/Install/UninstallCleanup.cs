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
/// The system account deletes nothing inside a user's profile on its own account.
/// <see cref="InstalledFootprint.PlanDataRemoval"/> decides: a folder is deleted only for a user who is
/// signed in, and the delete runs under that user's own token, so it has no more rights than the folder's
/// owner whatever the folder turns out to point at. A link anywhere between the profile and the folder
/// leaves it alone, checked again under the owner's token just before the delete. A user who is not
/// signed in keeps their folder; it is counted in what this prints, which the installer writes into its
/// log, and the operator doc says that the in-app "Delete everything" is how a technician erases their
/// own data (INV-12). What it prints is counts, never a path or a name (INV-10).
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

    /// <returns>0 when nothing was left, 1 when something was, 2 when it was not allowed to run.</returns>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // For the uninstaller, which runs it as the system account: holding another user's token needs
        // that account's privilege anyway. An ordinary user who found the flag gets a sentence and
        // nothing removed; "delete everything" for one person is the Settings button, which asks first.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                output.WriteLine("This step is run by the ScreenTail uninstaller as an administrator. Nothing was removed.");
                return 2;
            }
        }

        var installDirectory = AppContext.BaseDirectory;
        var stopped = StopInstalledCopies(installDirectory);

        var decisions = new Dictionary<DataFolderAction, int>();
        var failed = 0;
        foreach (var (action, ok) in RemoveDataFolders())
        {
            decisions[action] = decisions.GetValueOrDefault(action) + 1;
            failed += ok ? 0 : 1;
        }

        var (valuesRemoved, valuesLeft) = RemoveRunValues(installDirectory);

        var deleted = decisions.GetValueOrDefault(DataFolderAction.DeleteAsOwner) - failed;
        var notSignedIn = decisions.GetValueOrDefault(DataFolderAction.LeaveNotSignedIn);
        var linked = decisions.GetValueOrDefault(DataFolderAction.LeaveLinked);
        var unreadable = decisions.GetValueOrDefault(DataFolderAction.LeaveUnreadablePath);
        output.WriteLine($"ScreenTail uninstall: {stopped} running copies stopped.");
        output.WriteLine($"ScreenTail uninstall: {deleted} data folders deleted as their signed-in owner, {failed} could not be deleted.");
        output.WriteLine(
            $"ScreenTail uninstall: left in place: {notSignedIn} data folders whose owner is not signed in, {linked} reached through a link, "
            + $"{unreadable} profiles whose path could not be read plainly. A technician erases their own data with Delete everything in Settings.");
        output.WriteLine($"ScreenTail uninstall: {valuesRemoved} Run values removed, {valuesLeft} could not be removed.");

        return failed + notSignedIn + linked + unreadable + valuesLeft == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every service and UI started from this installation, in every session. The installer has already
    /// closed them; this is for one that started since, and it is what lets a store be deleted, since an
    /// open database file cannot be.
    /// </summary>
    private static int StopInstalledCopies(string installDirectory)
    {
        var stopped = 0;
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    // The kernel's record of the image, not the process's own (ProcessImage says why). A
                    // copy of ScreenTail running from anywhere else is not this installation's to stop.
                    using var image = ProcessImage.Of((uint)process.Id);
                    if (image is null || !InstalledFootprint.IsInside(image.Path, installDirectory))
                    {
                        continue;
                    }

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
        }

        return stopped;
    }

    /// <summary>Each profile's decision, and for a delete whether it happened.</summary>
    private static IEnumerable<(DataFolderAction Action, bool Done)> RemoveDataFolders()
    {
        var owners = SignedInUsers();
        try
        {
            var plan = InstalledFootprint.PlanDataRemoval(Profiles(), owners.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), Probe);
            foreach (var decision in plan)
            {
                if (decision.Action != DataFolderAction.DeleteAsOwner)
                {
                    yield return (decision.Action, true);
                    continue;
                }

                var done = WindowsIdentity.RunImpersonated(owners[decision.Sid].AccessToken, () => DeleteAsOwner(decision.Profile!, decision.Folder!));
                yield return (decision.Action, done);
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
    /// Runs as the folder's owner. The link check is asked again here, under the owner's token, and the
    /// delete only follows it; whatever happens between the two, the delete can do nothing its owner
    /// could not. .NET's recursive delete removes a link it meets inside rather than descending into it.
    /// </summary>
    private static bool DeleteAsOwner(string profile, string folder)
    {
        if (InstalledFootprint.BelowProfile(profile).Select(Probe).Contains(PathState.Link))
        {
            return false;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported as could-not-delete; the owner's own Delete everything is the way out.
        }

        return !Directory.Exists(folder);
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
