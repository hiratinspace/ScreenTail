using System.IO;
using Microsoft.Win32;

namespace ScreenTail.UI.Shell;

/// <summary>
/// Spec §5 S5 "Start UI at login": the UI process in the technician's Run key (ST-080). The UI rather
/// than the service, because the service is a Windows service and this is the tray icon and the pill.
/// Nothing here is content; a failure to write is reported, not thrown, because a settings save that
/// took should not look like one that did not.
/// </summary>
internal static class StartAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "ScreenTail";

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
