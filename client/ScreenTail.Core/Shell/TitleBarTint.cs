using System.Globalization;

namespace ScreenTail.Core.Shell;

/// <summary>
/// The colours a window's title bar is given (Spec v0.6.0 §3 Frame, built v0.6.12): the frame's own
/// <c>shell.bg</c> under <c>shell.text</c>, set through DWM's caption, text and border colour attributes,
/// so the title bar is part of the frame the rail and the pill are drawn on. Flat, no Mica or blur.
/// In high contrast every value is the system's own (<see cref="SystemDefault"/>), so Windows draws the
/// title bar in the contrast theme the technician chose.
/// </summary>
/// <param name="Caption">DWMWA_CAPTION_COLOR, a COLORREF (0x00BBGGRR), or <see cref="SystemDefault"/>.</param>
/// <param name="Text">DWMWA_TEXT_COLOR.</param>
/// <param name="Border">DWMWA_BORDER_COLOR: the window's 1 px edge, the frame's colour too.</param>
/// <param name="DarkCaptionButtons">
/// DWMWA_USE_IMMERSIVE_DARK_MODE: light minimise, maximise and close glyphs, for a dark caption. The frame
/// is dark in both themes (indigo navy in light), so this is on in both and off in high contrast.
/// </param>
public sealed record TitleBarTint(uint Caption, uint Text, uint Border, bool DarkCaptionButtons)
{
    /// <summary>DWMWA_COLOR_DEFAULT: the system draws this colour itself.</summary>
    public const uint SystemDefault = 0xFFFFFFFF;

    /// <summary>The system's own title bar, as high contrast needs.</summary>
    public static TitleBarTint System { get; } = new(SystemDefault, SystemDefault, SystemDefault, DarkCaptionButtons: false);

    /// <param name="highContrast">Whether the high-contrast dictionary is the one in use.</param>
    /// <param name="frame">The theme's <c>shell.bg</c>, as <c>#RRGGBB</c>.</param>
    /// <param name="text">The theme's <c>shell.text</c>, as <c>#RRGGBB</c>.</param>
    public static TitleBarTint For(bool highContrast, string frame, string text)
    {
        if (highContrast)
        {
            return System;
        }

        var caption = ColorRef(frame);
        return new TitleBarTint(caption, ColorRef(text), caption, DarkCaptionButtons: RelativeLuminance(frame) < 0.18);
    }

    /// <summary><c>#RRGGBB</c> as the COLORREF DWM takes: red in the low byte.</summary>
    public static uint ColorRef(string hex)
    {
        var (r, g, b) = Rgb(hex);
        return r | ((uint)g << 8) | ((uint)b << 16);
    }

    private static (byte R, byte G, byte B) Rgb(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        if (hex.Length != 7 || hex[0] != '#')
        {
            throw new FormatException($"'{hex}' is not #RRGGBB.");
        }

        return (
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>WCAG relative luminance. Below 0.18 white glyphs are the more readable of white and black.</summary>
    private static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }
}
