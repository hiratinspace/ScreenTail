using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using ScreenTail.Core.Install;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Service.Install;

/// <summary>
/// The uninstaller's own step (ST-112, ADR-0007): what an installed ScreenTail leaves outside its folder,
/// removed once for the whole machine.
///
/// Windows Installer removes what it installed: the files, the machine Run value, the shortcut. It does
/// not know about what the product made afterwards, per user: every technician's data folder (the
/// encrypted store, its key, the device's refresh token, the speech models, settings, crash reports) and
/// the Run value the UI writes for "Start UI at login". The ticket says an uninstall leaves nothing, and
/// INV-12 says the tokens go with the data, so the installer runs this, as the system account, after it
/// has closed every running copy and before it removes the files. Never during an upgrade: the package's
/// condition sees to that, and InstallerAuthoringTests holds it.
///
/// It deletes in folders that other users own, with the system account's rights, so it is careful about
/// what it follows. Every path is worked out by <see cref="InstalledFootprint"/>, which skips anything
/// it cannot read plainly; a folder that is a link, or sits under one, is left alone; the delete never
/// descends into a link; and where Windows offers it, the process refuses to follow junctions that an
/// ordinary user made. What it prints is counts, never a path or a name (INV-10).
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

    /// <returns>0 when everything is gone, 1 when something was left, 2 when it was not allowed to run.</returns>
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // For the uninstaller, which runs it as the system account. An ordinary user who found the flag
        // gets a sentence and nothing removed: "delete everything" for one person is the Settings button,
        // which asks first.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                output.WriteLine("This step is run by the ScreenTail uninstaller as an administrator. Nothing was removed.");
                return 2;
            }
        }

        var guarded = RedirectionGuard.TryEnable();
        var installDirectory = AppContext.BaseDirectory;

        var stopped = StopInstalledCopies(installDirectory);
        var (foldersRemoved, foldersLeft) = RemoveDataFolders();
        var (valuesRemoved, valuesLeft) = RemoveRunValues(installDirectory);

        output.WriteLine(
            $"ScreenTail uninstall: {stopped} running copies stopped, {foldersRemoved} data folders removed, {foldersLeft} left, "
            + $"{valuesRemoved} Run values removed, {valuesLeft} left, junction guard {(guarded ? "on" : "not available on this Windows")}.");
        return foldersLeft == 0 && valuesLeft == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every service and UI started from this installation, in every session. The installer has already
    /// asked them to close; this is for one that started since, and it is what lets the store be deleted,
    /// since an open database file cannot be.
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

    private static (int Removed, int Left) RemoveDataFolders()
    {
        var removed = 0;
        var left = 0;
        foreach (var (profile, data) in DataFolders())
        {
            if (!Directory.Exists(data))
            {
                continue;
            }

            if (PassesThroughALink(profile, data))
            {
                left++;
                continue;
            }

            if (TryDelete(data))
            {
                removed++;
            }
            else
            {
                left++;
            }
        }

        return (removed, left);
    }

    /// <summary>Every profile Windows has on this machine, paired with its ScreenTail data folder.</summary>
    private static IEnumerable<(string Profile, string Data)> DataFolders()
    {
        using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
        if (list is null)
        {
            yield break;
        }

        foreach (var sid in list.GetSubKeyNames())
        {
            using var entry = list.OpenSubKey(sid);

            // REG_EXPAND_SZ, expanded by GetValue. InstalledFootprint skips anything that is still not a
            // plain local folder after that.
            var profile = entry?.GetValue("ProfileImagePath") as string;
            foreach (var data in InstalledFootprint.DataDirectories([profile]))
            {
                yield return (profile!.Trim(), data);
            }
        }
    }

    /// <summary>
    /// Whether any folder from just below the profile down to the data folder is a link (a junction or a
    /// symbolic link). The profile folder itself is Windows' own; everything under it is the user's, and
    /// a link there would point this delete at somebody else's files.
    /// </summary>
    private static bool PassesThroughALink(string profile, string data)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        for (var current = new DirectoryInfo(data); current is not null; current = current.Parent)
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        // Walked to the drive without meeting the profile: not a folder under it at all.
        return true;
    }

    /// <summary>
    /// Deletes the folder and everything in it. .NET's recursive delete removes a link it meets instead
    /// of descending into it. A read-only file stops it, so a first failure clears that attribute on
    /// what is left (without following links either) and tries once more.
    /// </summary>
    private static bool TryDelete(string folder)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    ClearReadOnly(folder);
                }
            }
        }

        return !Directory.Exists(folder);
    }

    private static void ClearReadOnly(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    file.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The retry reports what is left.
        }
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

    /// <summary>
    /// Windows 11's redirection guard: this process stops following file-system junctions that a
    /// non-administrator created. It is the part of the careful delete that does not depend on checking
    /// first and deleting second. Older Windows does not have it, and says so by refusing the call.
    /// </summary>
    private static class RedirectionGuard
    {
        private const int ProcessRedirectionTrustPolicy = 16;
        private const uint EnforceRedirectionTrust = 1;

        public static bool TryEnable()
        {
            var flags = EnforceRedirectionTrust;
            return SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref flags, sizeof(uint));
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);
    }
}
