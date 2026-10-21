using System.Collections.Frozen;

namespace ScreenTail.Core.Detection;

/// <summary>
/// The Windows shell's own surfaces: the desktop, the taskbar, Task View and their kin (2026-10-12 review, S-2).
///
/// They belong to explorer.exe, the same process as a File Explorer folder window, and none of them is a
/// window a technician works in. The desktop is the one that matters: its rectangle is every monitor, and
/// the capturer copies a window's rectangle off the screen, so a frame "of the desktop" is a frame of every
/// window in front of it. Exclusions are judged on the window in front, which here is the desktop, so a
/// password manager or an inbox behind it would be photographed with nothing to stop it.
///
/// This list lives in code rather than in the registry on purpose: the registry can be edited, and this is
/// a floor no entry may lower.
/// </summary>
public static class ShellWindows
{
    /// <summary>The shell's process, which also draws File Explorer's folder windows.</summary>
    public const string ProcessName = "explorer";

    /// <summary>
    /// Compared without regard to case, the way Windows registers class names. Unknown classes are not in
    /// here and need no entry: an explorer window that is not a folder window is out of scope by the
    /// registry's own class list, and this set is what stays refused when "all windows" is on.
    /// </summary>
    private static readonly FrozenSet<string> Classes = new[]
    {
        // The desktop: Progman, and the WorkerW Windows puts behind it for the wallpaper.
        "Progman",
        "WorkerW",

        // The taskbar, on the main monitor and on each of the others.
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",

        // Task View and Alt+Tab, full-screen overlays on Windows 10 and 11, and the staging window behind them.
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "TaskSwitcherWnd",
        "ForegroundStaging",

        // The notification area's overflow flyout.
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this window class is one of the shell's own surfaces. A dictionary lookup per foreground change.</summary>
    public static bool IsShell(string? className) => className is { Length: > 0 } && Classes.Contains(className);

    /// <summary>
    /// Whether this window is one of the shell's surfaces, or a window of the shell's process whose class
    /// could not be read. Nothing shows the second kind is not the desktop, so it is refused the same way,
    /// which keeps "all windows" from reaching it. Other apps' windows are judged by class alone.
    /// </summary>
    public static bool IsShell(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return IsShell(window.ClassName)
            || (string.IsNullOrWhiteSpace(window.ClassName) && string.Equals(window.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase));
    }
}
