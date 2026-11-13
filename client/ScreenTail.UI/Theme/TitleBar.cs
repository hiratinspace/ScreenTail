using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Theme;

/// <summary>
/// Spec v0.6.0 §3 Frame (built v0.6.12): every window's title bar is tinted with the frame's colours, so
/// the title bar, the rail and the pill read as one frame, and it carries the app glyph (the window's
/// icon, <c>Window.Base</c>). Set on <c>Window.Base</c>, so every window built on it takes part; re-applied
/// whenever the theme changes. In high contrast the title bar is the system's own.
/// </summary>
/// <remarks>
/// DWM's caption colour attributes exist from Windows 11 (build 22000). On Windows 10 the calls return an
/// error and the title bar stays as Windows draws it, which is the right fallback: nothing else changes.
/// </remarks>
internal static class TitleBar
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.RegisterAttached(
        "Tint", typeof(bool), typeof(TitleBar), new PropertyMetadata(false, OnTintChanged));

    /// <summary>What was last asked of DWM for a window, for the Windows suite to read; never logged.</summary>
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
        "Applied", typeof(TitleBarTint), typeof(TitleBar), new PropertyMetadata(null));

    private static readonly List<WeakReference<Window>> Tinted = [];

    static TitleBar() => ThemeManager.Applied += (_, _) => ApplyAll();

    public static bool GetTint(Window window) => (bool)window.GetValue(TintProperty);

    public static void SetTint(Window window, bool value) => window.SetValue(TintProperty, value);

    /// <summary>The colours last sent for this window, or null before its handle exists.</summary>
    public static TitleBarTint? GetApplied(Window window) => (TitleBarTint?)window.GetValue(AppliedProperty);

    /// <summary>What the window's title bar takes in the theme now in use.</summary>
    public static TitleBarTint Current(FrameworkElement window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (ThemeManager.Current == AppTheme.HighContrast
            || window.TryFindResource("Token.shell.bg") is not Color frame
            || window.TryFindResource("Token.shell.text") is not Color text)
        {
            return TitleBarTint.System;
        }

        return TitleBarTint.For(highContrast: false, Hex(frame), Hex(text));
    }

    private static void OnTintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
        {
            return;
        }

        lock (Tinted)
        {
            Tinted.Add(new WeakReference<Window>(window));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply(window);
        }
    }

    private static void ApplyAll()
    {
        List<Window> windows;
        lock (Tinted)
        {
            _ = Tinted.RemoveAll(w => !w.TryGetTarget(out _));
            windows = [.. Tinted.Select(w => w.TryGetTarget(out var window) ? window : null).OfType<Window>()];
        }

        foreach (var window in windows)
        {
            if (window.Dispatcher.CheckAccess())
            {
                Apply(window);
            }
            else
            {
                _ = window.Dispatcher.BeginInvoke(() => Apply(window));
            }
        }
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var tint = Current(window);
        var dark = tint.DarkCaptionButtons ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var caption = tint.Caption;
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(uint));
        var text = tint.Text;
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(uint));
        var border = tint.Border;
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(uint));
        window.SetValue(AppliedProperty, tint);
    }

    private static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
