using System.Windows;
using Microsoft.Win32;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Theme;

public enum AppTheme
{
    Dark,
    Light,
    HighContrast,
}

/// <summary>
/// Swaps the colour dictionary (Spec §2.1) at runtime. Components bind with DynamicResource, so a theme
/// change touches no component. Windows high contrast wins over the requested theme (Spec §7).
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _colors;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Spec §2.3: honour the OS "reduce motion" (Windows: "Show animations") setting.</summary>
    public static bool ReduceMotion => !ClientAreaAnimation();

    public static AppTheme Effective(AppTheme requested) => SystemHighContrast() ? AppTheme.HighContrast : requested;

    /// <summary>
    /// Where the two Windows settings are read. Seams for the accessibility suite (ST-084), which checks
    /// what the app does with each setting without changing the machine's: a test that switched Windows
    /// animations or high contrast off and on would change them for everything else on the desktop.
    /// </summary>
    internal static Func<bool> ClientAreaAnimation { get; set; } = () => SystemParameters.ClientAreaAnimation;

    /// <inheritdoc cref="ClientAreaAnimation"/>
    internal static Func<bool> SystemHighContrast { get; set; } = () => SystemParameters.HighContrast;

    /// <summary>
    /// Windows' "Choose your app mode" (Settings, Personalization, Colors): true for Light. Windows keeps
    /// it as <c>AppsUseLightTheme</c>; a value that is missing or cannot be read means Light, as it does
    /// for Windows itself. A seam, like the two above, so a test never changes the machine's setting.
    /// </summary>
    internal static Func<bool> WindowsUsesLight { get; set; } = ReadAppMode;

    /// <summary>
    /// The technician's Settings, Appearance choice, or null while a render, the gallery or a test has
    /// set a theme of its own with <see cref="Apply"/>. Only <see cref="ThemeChoice.System"/> follows
    /// Windows' app mode when it changes.
    /// </summary>
    public static ThemeChoice? Choice { get; private set; }

    /// <summary>The palette <paramref name="choice"/> shows with Windows set as it is now.</summary>
    public static AppTheme Resolve(ThemeChoice choice) =>
        ThemeChoices.UsesLight(choice, WindowsUsesLight()) ? AppTheme.Light : AppTheme.Dark;

    /// <summary>Applies the technician's choice (ST-070): at startup, and when Settings saves one.</summary>
    public static void Follow(ThemeChoice choice, ResourceDictionary resources)
    {
        Choice = ThemeChoices.Known(choice);
        Set(Resolve(Choice.Value), resources);
    }

    /// <summary>
    /// What Windows' app mode is set to now, in the words Settings shows beside "Use my Windows setting".
    /// </summary>
    public static string WindowsModeName() => WindowsUsesLight() ? "Light" : "Dark";

    private static bool ReadAppMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// The palette asked for, which is not always what is on screen: high contrast takes its place while
    /// Windows has it on, and this stays as it was, so turning high contrast off puts it back. What is
    /// saved is <see cref="Choice"/>, never the substitution.
    /// </summary>
    public static AppTheme Requested { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// Switches to high contrast the moment Windows does, and back when it stops.
    ///
    /// Without this the substitution only happened at startup: a technician who turned high contrast on
    /// mid-session (Left Alt+Left Shift+PrtScn, which is a shortcut people hit by accident) kept the
    /// dark palette until they restarted the app. That is the population least able to work around it.
    ///
    /// The same for Windows' app mode while the technician's choice is System (ST-070): Windows says a
    /// "General" preference changed, and the palette follows if the mode is now different. That event
    /// arrives on a thread of its own, so the swap is sent to the UI thread.
    /// </summary>
    public static void FollowSystem(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.HighContrast))
            {
                Set(Requested, resources);
            }
        };

        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                dispatcher.BeginInvoke(() => ModeChanged(resources));
            }
        };
    }

    /// <summary>Windows' app mode may have changed: a System choice shows the palette it now asks for.</summary>
    internal static void ModeChanged(ResourceDictionary resources)
    {
        if (Choice is not ThemeChoice.System)
        {
            return;
        }

        var next = Resolve(ThemeChoice.System);
        if (next != Requested)
        {
            Set(next, resources);
        }
    }

    /// <summary>
    /// Applies a theme, substituting high contrast whenever Windows asks for it (Spec §7).
    ///
    /// The substitution lives here rather than at the call site because it was at the call site, and
    /// exactly one of three callers remembered it: the gallery's theme buttons replaced the high-contrast
    /// palette with the dark one, which is the opposite of what a high-contrast user needs and no
    /// compiler would ever mention.
    /// </summary>
    /// <remarks>
    /// A theme set here is fixed: it is not the technician's choice, so Windows' app mode no longer moves
    /// it. Renders, the gallery and the tests set theirs this way.
    /// </remarks>
    public static void Apply(AppTheme theme, ResourceDictionary resources)
    {
        Choice = null;
        Set(theme, resources);
    }

    private static void Set(AppTheme theme, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Requested = theme;
        theme = Effective(theme);
        var file = theme switch
        {
            AppTheme.Dark => "Tokens.Dark.g.xaml",
            AppTheme.Light => "Tokens.Light.g.xaml",
            AppTheme.HighContrast => "Tokens.HighContrast.xaml",
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
        };

        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/ScreenTail.UI;component/Theme/{file}") };
        if (_colors is not null)
        {
            resources.MergedDictionaries.Remove(_colors);
        }

        resources.MergedDictionaries.Insert(0, next);
        _colors = next;
        Current = theme;
        Applied?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// After a theme's colours are in place: what the dictionaries cannot reach listens here, such as the
    /// title bar's tint, which DWM draws outside WPF (<see cref="TitleBar"/>, v0.6.12).
    /// </summary>
    public static event EventHandler? Applied;

    public static Duration Motion(Duration requested) => ReduceMotion ? new Duration(TimeSpan.Zero) : requested;
}
