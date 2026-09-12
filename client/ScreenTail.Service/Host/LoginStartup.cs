using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ScreenTail.Service.Host;

/// <summary>
/// Starts the service (and optionally the UI) at sign-in through the user's Run key: inside the interactive
/// session, which is where hooks and capture work (ADR-0003). The installer (ST-112) owns this eventually.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LoginStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ServiceValueName = "ScreenTail.Service";
    public const string UiValueName = "ScreenTail.UI";

    public static void Register(string valueName, string executable, string arguments = "")
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var command = $"\"{Path.GetFullPath(executable)}\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public static void Unregister(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static bool IsRegistered(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(valueName) is string;
    }
}
