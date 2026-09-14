using System.Windows;
using ScreenTail.Core.Review;

namespace ScreenTail.UI.Review;

/// <summary>
/// The typed gate in front of Discard (Spec §3, §5 S3), and in front of a bulk discard (§5 S4).
///
/// Whether the typing is right is <see cref="TypedConfirmation"/>'s job, which is where the tests are.
/// This shows the words and keeps the button disabled until it agrees.
/// </summary>
public partial class ConfirmTypedDialog : Window
{
    private readonly TypedConfirmation _confirmation;

    public ConfirmTypedDialog(TypedConfirmation confirmation, string heading, string consequence)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        _confirmation = confirmation;
        InitializeComponent();

        // A named type, not an anonymous one: anonymous types are internal, and WPF binding to them
        // depends on reflection permissions that have bitten this pattern before. The failure is a blank
        // dialog with a disabled button, which is the last place to discover a quirk.
        DataContext = new Words(heading, consequence, confirmation.Prompt);
    }

    /// <summary>What the dialog says. Public so the binding engine can read it without help.</summary>
    public sealed record Words(string Heading, string Consequence, string Prompt);

    /// <summary>Spec §5 S3's Discard: the raw data goes now, and the audit row records that it did.</summary>
    public static bool AskToDiscard(Window? owner) =>
        new ConfirmTypedDialog(
            TypedConfirmation.ForDiscard(),
            "Discard this session?",
            "The screenshots, transcript and timeline are deleted from this device straight away, and the note goes with them. This cannot be undone.")
        { Owner = owner }.ShowDialog() == true;

    private void OnTyped(object sender, RoutedEventArgs e) => Confirm.IsEnabled = _confirmation.Accepts(Typed.Text);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        // Checked again rather than trusting the button: the button's enabled state is a view of the rule,
        // and a rule enforced only by what is clickable is enforced only until something else clicks it.
        if (_confirmation.Accepts(Typed.Text))
        {
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
