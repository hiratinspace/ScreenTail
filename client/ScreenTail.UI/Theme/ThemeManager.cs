using System.Windows;

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
    public static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    public static AppTheme Effective(AppTheme requested) => SystemParameters.HighContrast ? AppTheme.HighContrast : requested;

    /// <summary>
    /// What the technician asked for, which is not always what is on screen. Persisted by ST-070, so it
    /// has to be their choice rather than the high-contrast substitution — otherwise turning Windows
    /// high contrast on once would silently rewrite their saved preference to HighContrast for ever.
    /// </summary>
    public static AppTheme Requested { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// Switches to high contrast the moment Windows does, and back when it stops.
    ///
    /// Without this the substitution only happened at startup: a technician who turned high contrast on
    /// mid-session — Left Alt+Left Shift+PrtScn, which is a shortcut people hit by accident — kept the
    /// dark palette until they restarted the app. That is the population least able to work around it.
    /// </summary>
    public static void FollowSystem(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.HighContrast))
            {
                Apply(Requested, resources);
            }
        };
    }

    /// <summary>
    /// Applies a theme, substituting high contrast whenever Windows asks for it (Spec §7).
    ///
    /// The substitution lives here rather than at the call site because it was at the call site, and
    /// exactly one of three callers remembered it: the gallery's theme buttons replaced the high-contrast
    /// palette with the dark one, which is the opposite of what a high-contrast user needs and no
    /// compiler would ever mention.
    /// </summary>
    public static void Apply(AppTheme theme, ResourceDictionary resources)
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
    }

    public static Duration Motion(Duration requested) => ReduceMotion ? new Duration(TimeSpan.Zero) : requested;
}
