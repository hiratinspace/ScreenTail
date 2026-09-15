using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ScreenTail.Core.Hud;

namespace ScreenTail.UI.Hud;

/// <summary>
/// Maps a <see cref="HudTone"/> to the design token that colours it (ST-072, Spec §2 tokens).
///
/// A converter rather than four triggers, because the mapping is the spec's and belongs in one readable
/// place. The colour is never the only signal — the glyph and the words carry the state too (Spec §7) —
/// so a technician with no colour vision reads the same pill.
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            HudTone.Recording => "Brush.state.recording",
            HudTone.Paused => "Brush.state.paused",
            HudTone.Scope => "Brush.state.scope",
            _ => "Brush.text.muted",
        };

        // DynamicResource semantics by hand: the themes swap these dictionaries at runtime, and a brush
        // captured once would keep the old theme's colour after a switch.
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The pill's colour is derived from its state, never the other way round.");
}
