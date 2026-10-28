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
    private sealed class Tally
    {
        public int Deleted { get; set; }

        public int PartlyDeleted { get; set; }

        public int Shredded { get; set; }

        public int SecretsRemoved { get; set; }

        public int SecretsInDoubt { get; set; }

        public int Linked { get; set; }

        public int Unreadable { get; set; }
    }

    /// <returns>0 when nothing was left in doubt, 1 when something was, 2 when it was not allowed to run.</returns>
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

        output.WriteLine($"ScreenTail uninstall: {stopped} running copies stopped.");
        output.WriteLine(
            $"ScreenTail uninstall: signed in: {tally.Deleted} data folders deleted with their owner's rights, "
            + $"{tally.PartlyDeleted} only partly (key and credentials gone, the rest left).");
        output.WriteLine(
            $"ScreenTail uninstall: not signed in: {tally.Shredded} data folders had their key and credentials removed "
            + $"({tally.SecretsRemoved} files), so the store left in them cannot be read; {tally.SecretsInDoubt} files left in doubt.");
        output.WriteLine(
            $"ScreenTail uninstall: left alone: {tally.Linked} reached through a link, {tally.Unreadable} profiles whose path could not be read plainly.");
        output.WriteLine($"ScreenTail uninstall: {valuesRemoved} Run values removed, {valuesLeft} could not be removed.");

        return tally.PartlyDeleted + tally.SecretsInDoubt + tally.Linked + tally.Unreadable + valuesLeft == 0 ? 0 : 1;
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
                            Shred(decision.Profile!, tally);
                        }

                        break;
                    case DataFolderAction.ShredSecrets:
                        Shred(decision.Profile!, tally);
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

    private static void Shred(string profile, Tally tally)
    {
        var outcomes = SecretShredder.Shred(profile);
        if (outcomes.All(o => o == SecretShredder.Outcome.Absent))
        {
            return;
        }

        tally.Shredded++;
        tally.SecretsRemoved += outcomes.Count(o => o == SecretShredder.Outcome.Removed);
        tally.SecretsInDoubt += outcomes.Count(o => o == SecretShredder.Outcome.LeftInDoubt);
    }

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
