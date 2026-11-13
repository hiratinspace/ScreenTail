namespace ScreenTail.Core.Shell;

/// <summary>
/// Which brand tray icon shows a state on which taskbar (Spec §2.0, tray glyph tiers).
///
/// The icons are built from <c>shared/design/brand/tray-*.svg</c> by <c>build-icons.py</c> into
/// <c>client/ScreenTail.UI/Assets/Tray</c>, one .ico per state and taskbar with a frame for each scale.
/// The light taskbar gets the Iris glyph, the dark one Ribbon 400, because Iris is only 2.49:1 on the dark
/// taskbar. Each state adds a shape as well as a colour: a dot, two bars, a cross, a square badge.
/// No WPF or Windows types, so the choice is tested on any OS.
/// </summary>
public static class TrayGlyph
{
    /// <summary>The file name's state segment: <c>tray-&lt;state&gt;-&lt;light|dark&gt;.ico</c>.</summary>
    public static string StateName(TrayIcon icon) => icon switch
    {
        TrayIcon.Idle => "idle",
        TrayIcon.Recording => "recording",
        TrayIcon.Paused => "paused",
        TrayIcon.DraftReady => "draftready",
        TrayIcon.Offline => "offline",
        _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, "A tray state with no icon."),
    };

    /// <summary>The icon's file name, which is also the last part of its embedded resource name.</summary>
    public static string FileName(TrayIcon icon, bool lightTaskbar) =>
        $"tray-{StateName(icon)}-{(lightTaskbar ? "light" : "dark")}.ico";

    /// <summary>
    /// Whether the taskbar is light, from <c>SystemUsesLightTheme</c> under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>.
    ///
    /// 1 is light. Anything else is dark, including a missing value: Windows 10 before 1903 had no light
    /// taskbar and no such value, and the dark glyph is the one Windows itself defaults to.
    /// </summary>
    public static bool IsLightTaskbar(object? systemUsesLightTheme) => systemUsesLightTheme is int value && value == 1;

    public static bool IsLightTaskbar(object? systemUsesLightTheme, bool highContrast, bool windowIsLight) => IsLightTaskbar(systemUsesLightTheme);
}
