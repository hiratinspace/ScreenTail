namespace ScreenTail.UI.Render;

/// <summary>
/// The themes the harness renders. Light and dark are the product's own; the two contrast themes are
/// fixed palettes (<see cref="ContrastPalette"/>) laid over the high-contrast dictionary, so the renders
/// show black and white contrast whatever the runner's own settings are.
/// </summary>
internal enum RenderTheme
{
    Dark,
    Light,
    HcBlack,
    HcWhite,
}

/// <summary>What a render theme is called in a file name, and which palette it draws with. No WPF types.</summary>
internal static class RenderThemes
{
    /// <summary>In the order every harness renders them and the index lists them.</summary>
    public static IReadOnlyList<RenderTheme> All { get; } = [RenderTheme.Dark, RenderTheme.Light, RenderTheme.HcBlack, RenderTheme.HcWhite];

    /// <summary>The last segment of every render's file name: <c>shell-review-hcblack.png</c>.</summary>
    public static string Suffix(this RenderTheme theme) => theme switch
    {
        RenderTheme.Dark => "dark",
        RenderTheme.Light => "light",
        RenderTheme.HcBlack => "hcblack",
        RenderTheme.HcWhite => "hcwhite",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null),
    };

    /// <summary>The fixed contrast palette, or null for the product's own light and dark.</summary>
    public static ContrastPalette? Palette(this RenderTheme theme) => theme switch
    {
        RenderTheme.HcBlack => ContrastPalette.NightSky,
        RenderTheme.HcWhite => ContrastPalette.Desert,
        _ => null,
    };

    /// <summary>A render's file name: its screen, then its theme, then <c>.png</c>.</summary>
    public static string FileName(string screen, RenderTheme theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screen);
        return $"{screen}-{theme.Suffix()}.png";
    }
}
