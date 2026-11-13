using System.Drawing;
using System.Runtime.Versioning;
using ScreenTail.UI.Theme;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace ScreenTail.UI.Tray;

/// <summary>
/// The tray menu drawn in the app's tokens (Spec §2.1, §3; S1 as built v0.6.10).
///
/// Windows Forms' default renderer drew the menu in its own palette, with a gradient down the image
/// margin, which §2.0's flat-fill rule and §2.1's tokens both rule out, and its greyed disabled text fell
/// under 4.5:1. This reads every colour from the theme dictionary the windows use, at paint time, so the
/// menu follows a theme change and, in high contrast, the system colours the dictionary maps to: there
/// the row under the pointer or the keyboard is the Highlight fill with HighlightText, as in every other
/// Windows menu, because the tints are all the window colour.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayMenuRenderer : Forms.ToolStripRenderer
{
    /// <summary>The colour a token has in the theme on show, or the system colour it stands for when no theme is loaded.</summary>
    internal static Color Token(string name)
    {
        if (System.Windows.Application.Current?.TryFindResource($"Token.{name}") is Media.Color colour)
        {
            return Color.FromArgb(colour.A, colour.R, colour.G, colour.B);
        }

        // A tray built with no WPF application (the Windows unit tests): the system colours the
        // high-contrast dictionary maps each token to, which are always legible together.
        return name switch
        {
            "bg.surface" or "bg.hover" or "bg.selected" => SystemColors.Window,
            "text.primary" or "text.secondary" => SystemColors.WindowText,
            "text.muted" => SystemColors.GrayText,
            "accent.primary" => SystemColors.Highlight,
            "text.on-accent" => SystemColors.HighlightText,
            "border.subtle" => SystemColors.GrayText,
            _ => SystemColors.WindowFrame,
        };
    }

    /// <summary>Whether the theme on show is high contrast, where the tints are not seen.</summary>
    internal static bool HighContrast => ThemeManager.Current == AppTheme.HighContrast || System.Windows.Application.Current is null;

    /// <summary>Whether the menu's ground is light, which picks the light-taskbar glyph for the state line.</summary>
    internal static bool LightGround => Token("bg.surface").GetBrightness() > 0.5f;

    /// <summary>What the text of a row is drawn in: the selection's text, the state line, a hint, or a disabled item.</summary>
    internal static Color TextColour(Forms.ToolStripItem item, bool hotkey)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Enabled && item.Selected && HighContrast)
        {
            return Token("text.on-accent");
        }

        return (item.Tag as TrayRowTag)?.Kind switch
        {
            Core.Shell.TrayRowKind.State => Token("text.primary"),
            Core.Shell.TrayRowKind.Hint => Token("text.secondary"),

            // A disabled row keeps its words at 4.5:1 (Spec §2.1, v0.5.0) rather than the system's grey.
            _ when !item.Enabled => Token("text.muted"),
            _ when hotkey => Token("text.secondary"),
            _ => Token("text.primary"),
        };
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var fill = new SolidBrush(Token("bg.surface"));
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
    {
        // Flat: the margin is the menu's own fill (§2.0, no gradients).
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var edge = new Pen(Token("border.strong"));
        var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
        e.Graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var item = e.Item;
        if (!item.Enabled || !(item.Selected || item.Pressed))
        {
            return;
        }

        // The row under the pointer or the arrow keys: the selection tint with a 2 px accent edge just
        // inside it (§3's focus ring, drawn inside as on a table row), so the keyboard's place is plain on
        // a white menu where the tint alone is faint. High contrast: the Highlight fill.
        var bounds = new Rectangle(2, 0, item.Width - 4, item.Height);
        using var fill = new SolidBrush(HighContrast ? Token("accent.primary") : Token("bg.selected"));
        e.Graphics.FillRectangle(fill, bounds);
        if (!HighContrast)
        {
            var scale = e.ToolStrip?.DeviceDpi / 96f ?? 1f;
            var width = Math.Max(2f, 2f * scale);
            using var ring = new Pen(Token("accent.primary"), width) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
            e.Graphics.DrawRectangle(ring, bounds);
        }
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var hotkey = e.Item is Forms.ToolStripMenuItem { ShortcutKeyDisplayString: { Length: > 0 } shown } && e.Text == shown;
        Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, TextColour(e.Item, hotkey), e.TextFormat);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var line = new Pen(Token("border.subtle"));
        var middle = e.Item.Height / 2;
        e.Graphics.DrawLine(line, 8, middle, e.Item.Width - 8, middle);
    }
}

/// <summary>What the host hangs on each menu item, so the renderer knows a state line from a hint from an item.</summary>
internal sealed record TrayRowTag(Core.Shell.TrayRowKind Kind, Core.Shell.TrayCommand Command);
