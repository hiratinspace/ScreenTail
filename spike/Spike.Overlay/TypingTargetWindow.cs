using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.Spike.Overlay;

/// <summary>
/// A plain, focusable text window for <c>Spike.Capture run --drive-input</c> to type into, so unattended
/// runs never type into anything else. Opened with <c>--typing-target</c>.
/// </summary>
public sealed class TypingTargetWindow : Window
{
    public const string TargetTitle = "ScreenTail.Spike.TypingTarget";

    public TypingTargetWindow()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 14,
        };

        // Keep the text small over long runs.
        box.TextChanged += (_, _) =>
        {
            if (box.Text.Length > 20_000)
            {
                box.Clear();
            }
        };

        Title = TargetTitle;
        Content = box;
        WindowState = WindowState.Maximized;
        Loaded += (_, _) => Keyboard.Focus(box);
        Activated += (_, _) => Keyboard.Focus(box);
    }
}
