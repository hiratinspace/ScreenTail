using System.IO;
using Microsoft.Win32;

namespace ScreenTrail.UI.Shell;

/// <summary>
/// Spec §5 S5 "Start UI at login": the UI process in the technician's Run key (ST-080). The UI rather
/// than the service, because the service is a Windows service and this is the tray icon and the pill.
/// Nothing here is content; a failure to write is reported, not thrown, because a settings save that
/// took should not look like one that did not.
/// </summary>
internal static class StartAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "ScreenTrail";

    /// <summary>The value's name before the product was renamed (ScreenTail, until 2026-11-17).</summary>
    private const string LegacyValueName = "ScreenTail";

    /// <summary>
    /// A technician who had "Start UI at login" on before the rename has a value under the old name that
    /// starts a program the upgrade removed. It is replaced by today's value pointing at this program, so
    /// the setting stays on; with no old value there is nothing to do. Called once at the window's start.
    /// </summary>
    public static void MigrateLegacyValue()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(LegacyValueName) is not string)
            {
                return;
            }

            run.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            run.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Left as it was: the setting can still be turned on again in Settings.
        }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                run.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
