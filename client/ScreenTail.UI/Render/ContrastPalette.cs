namespace ScreenTail.UI.Render;

/// <summary>
/// A fixed Windows contrast theme, for the render harness only.
///
/// The high-contrast dictionary maps every token to a <c>SystemColors</c> value, which is right for the
/// product: a technician in high contrast sees the palette they chose. It made the renders useless,
/// though. The hosted runner is not in high contrast, so the system colours it reported were the ordinary
/// light ones, and every <c>*-highcontrast.png</c> was a near-copy of the light render. Nobody had seen the
/// app in black or white contrast.
///
/// These are the two built-in Windows 11 themes most people in high contrast use, Night sky (black) and
/// Desert (white), as Settings → Accessibility → Contrast themes draws them. The harness lays them over the
/// high-contrast dictionary in place of the live system colours. Nothing outside the harness reads them:
/// the application's high contrast still follows the OS.
///
/// No WPF types here, so the tests can hold the table to the dictionary on any OS.
/// </summary>
internal sealed class ContrastPalette
{
    private ContrastPalette(string name, IReadOnlyDictionary<string, string> systemColours)
    {
        Name = name;
        SystemColours = systemColours;
    }

    /// <summary>
    /// Night sky: white on black, yellow controls, mauve selection, violet links. The "UI elements" yellow
    /// is the button text, and is used for the active border too, which is how the theme draws a control's
    /// outline.
    /// </summary>
    public static ContrastPalette NightSky { get; } = new("Night sky", new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Window"] = "#000000",
        ["WindowText"] = "#FFFFFF",
        ["WindowFrame"] = "#FFFFFF",
        ["Control"] = "#000000",
        ["ControlText"] = "#FFEE32",
        ["ActiveBorder"] = "#FFEE32",
        ["GrayText"] = "#A6A6A6",
        ["Highlight"] = "#D6B4FD",
        ["HighlightText"] = "#2B2B2B",
        ["HotTrack"] = "#8080FF",
    });

    /// <summary>Desert: dark grey on off-white, brown selection, blue links, onyx controls.</summary>
    public static ContrastPalette Desert { get; } = new("Desert", new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Window"] = "#FFFAEF",
        ["WindowText"] = "#3D3D3D",
        ["WindowFrame"] = "#3D3D3D",
        ["Control"] = "#FFFAEF",
        ["ControlText"] = "#202020",
        ["ActiveBorder"] = "#202020",
        ["GrayText"] = "#676767",
        ["Highlight"] = "#903909",
        ["HighlightText"] = "#FFF5E3",
        ["HotTrack"] = "#1C5E75",
    });

    /// <summary>
    /// The system colour each token takes in <c>Theme/Tokens.HighContrast.xaml</c>, by token name without
    /// its <c>Token.</c> or <c>Brush.</c> prefix. A test holds this to the dictionary, so a token remapped
    /// there and not here fails rather than rendering the old colour.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TokenSystemColours { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bg.base"] = "Window",
        ["bg.surface"] = "Window",
        ["bg.raised"] = "Control",
        ["bg.hover"] = "Window",
        ["bg.pressed"] = "Window",
        ["bg.selected"] = "Window",
        ["bg.warning"] = "Window",
        ["bg.danger"] = "Window",
        ["bg.success"] = "Window",
        ["border.subtle"] = "ActiveBorder",
        ["border.strong"] = "WindowFrame",
        ["text.primary"] = "WindowText",
        ["text.secondary"] = "WindowText",
        ["text.muted"] = "GrayText",
        ["accent.primary"] = "Highlight",
        ["accent.primary.hover"] = "HotTrack",
        ["state.recording"] = "WindowText",
        ["state.paused"] = "WindowText",
        ["state.scope"] = "WindowText",
        ["state.published"] = "WindowText",
        ["state.offline"] = "GrayText",
        ["state.danger"] = "WindowText",
        ["confidence.low"] = "WindowText",
        ["text.on-accent"] = "HighlightText",
    };

    public string Name { get; }

    /// <summary>Hex colour by <c>SystemColors</c> name without its <c>Color</c> suffix (<c>Window</c>, <c>WindowText</c>).</summary>
    public IReadOnlyDictionary<string, string> SystemColours { get; }

    /// <summary>Every token's colour in this palette, keyed as <see cref="TokenSystemColours"/> is.</summary>
    public IReadOnlyDictionary<string, string> Tokens() =>
        TokenSystemColours.ToDictionary(pair => pair.Key, pair => SystemColours[pair.Value], StringComparer.Ordinal);

    /// <summary>A hex colour as bytes.</summary>
    public static (byte R, byte G, byte B) Rgb(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        if (hex.Length != 7 || hex[0] != '#')
        {
            throw new FormatException($"'{hex}' is not #RRGGBB.");
        }

        return (
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }
}
