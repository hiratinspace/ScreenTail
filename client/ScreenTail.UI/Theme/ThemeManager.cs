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

    public static void Apply(AppTheme theme, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
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
