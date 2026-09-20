using System.Windows;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The typed confirmation the spec asks for before anything irreversible (Spec §3, ST-085).
///
/// "Discard session" sat one item below "Stop and draft" in the tray menu and threw the session away on a
/// single click; a misclick cost a technician the whole job (2026-09-19 review). Typing is the point:
/// agreeing should take an act rather than a reflex, and two adjacent menu items are a reflex.
///
/// <b>This is the courtesy, not the control.</b> The service refuses a destructive command that does not
/// carry a token it issued moments ago, because a dialog the UI is trusted to have shown is one a process
/// that is not the UI does not have to show. What this adds is that the person meant it.
/// </summary>
public partial class ConfirmWindow : Window
{
    private readonly string _phrase;

    /// <param name="phrase">What must be typed. The service decides it, so the words cannot drift apart.</param>
    public ConfirmWindow(string heading, string consequence, string phrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        InitializeComponent();

        _phrase = phrase;
        Title = heading;
        Heading.Text = heading;
        Consequence.Text = consequence;
        Instruction.Text = $"Type {phrase} to confirm.";
        Confirm.Content = heading;
    }

    /// <summary>
    /// Shows it and says whether the technician meant it.
    ///
    /// Modal, and only ever called when no session is recording: Spec §5 forbids a modal over a live
    /// session, because a dialog that steals focus mid-capture is a dialog in the screenshot.
    /// </summary>
    public static bool Asks(Window? owner, string heading, string consequence, string phrase)
    {
        var dialog = new ConfirmWindow(heading, consequence, phrase);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnTyped(object sender, System.Windows.Controls.TextChangedEventArgs e) =>

        // Exactly, and case-sensitively. A confirmation that accepts "discard" is one somebody types
        // without reading, which is the reflex this exists to interrupt.
        Confirm.IsEnabled = string.Equals(Typed.Text, _phrase, StringComparison.Ordinal);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
