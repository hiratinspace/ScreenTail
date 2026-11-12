using System.Windows;
using System.Windows.Media;
using ScreenTail.UI.Theme;

namespace ScreenTail.UI.Render;

/// <summary>
/// Applies a <see cref="RenderTheme"/> for a render. Light and dark go straight to
/// <see cref="ThemeManager"/>. The two contrast themes apply the high-contrast dictionary, then lay a
/// fixed palette over it: every token and brush, and the <c>SystemColors</c> keys themselves, so anything
/// that reads a system colour directly draws the palette too.
///
/// Only the harnesses call this. The running application never does, so its high contrast is whatever
/// Windows says it is.
/// </summary>
internal static class RenderThemeApplier
{
    private static ResourceDictionary? _overlay;

    public static void Apply(RenderTheme theme, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (_overlay is not null)
        {
            _ = resources.MergedDictionaries.Remove(_overlay);
            _overlay = null;
        }

        var palette = theme.Palette();
        if (palette is null)
        {
            ThemeManager.Apply(theme == RenderTheme.Light ? AppTheme.Light : AppTheme.Dark, resources);
            return;
        }

        ThemeManager.Apply(AppTheme.HighContrast, resources);
        var overlay = new ResourceDictionary();
        foreach (var (token, hex) in palette.Tokens())
        {
            var colour = ToColor(hex);
            overlay[$"Token.{token}"] = colour;
            overlay[$"Brush.{token}"] = Brush(colour);
        }

        foreach (var (system, hex) in palette.SystemColours)
        {
            var colour = ToColor(hex);
            overlay[Key(system + "ColorKey")] = colour;
            overlay[Key(system + "BrushKey")] = Brush(colour);
        }

        // Last, so it wins: a merged dictionary later in the list is searched first.
        resources.MergedDictionaries.Add(overlay);
        _overlay = overlay;
    }

    private static Color ToColor(string hex)
    {
        var (r, g, b) = ContrastPalette.Rgb(hex);
        return Color.FromRgb(r, g, b);
    }

    private static SolidColorBrush Brush(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static object Key(string name) =>
        typeof(SystemColors).GetProperty(name)?.GetValue(null)
        ?? throw new InvalidOperationException($"SystemColors has no {name}.");
}
