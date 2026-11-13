using System.Windows;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The app's one dialog (Spec §3 Modal, v0.6.11): every question the client asks, and the one notice it
/// cannot say beside a button, goes through it, so they all look the same and follow the same rules. The
/// words are <see cref="ConfirmCopy"/>'s, which is where the copy is tested.
///
/// With a <see cref="TypedConfirmation"/> it is the typed gate in front of anything irreversible (the
/// tray's and Review's Discard, bulk discard, Delete everything). "Discard session" sat one item below
/// "Stop and draft" in the tray menu and threw the session away on a single click; a misclick cost a
/// technician the whole job (2026-09-19 review). Typing is the point: agreeing should take an act rather
/// than a reflex. Whether the typing is right is <see cref="TypedConfirmation"/>'s job.
///
/// <b>This is the courtesy, not the control.</b> The service refuses a destructive command that does not
/// carry a token it issued moments ago, because a dialog the UI is trusted to have shown is one a process
/// that is not the UI does not have to show. What this adds is that the person meant it.
///
/// Modal, and only ever shown when no session is recording (Spec §4), but for the question Quit asks
/// about the session itself.
/// </summary>
public partial class ConfirmWindow : Window
{
    private readonly ConfirmWords _words;
    private readonly TypedConfirmation? _typed;

    /// <param name="words">What it asks and what its buttons say.</param>
    /// <param name="typed">What must be typed before the action is offered, or null for a plain question.</param>
    public ConfirmWindow(ConfirmWords words, TypedConfirmation? typed = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        _words = words;
        _typed = typed;
        InitializeComponent();
        DataContext = words;

        if (FindResource(words.Tone == ConfirmTone.Danger ? "Button.Danger" : "Button.Primary") is Style style)
        {
            Confirm.Style = style;
        }

        // Warning (E7BA) for danger and for a notice, info (E946) for any other question.
        Mark.Text = words.Action is null || words.Tone == ConfirmTone.Danger ? "" : "";
        Mark.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            words.Action is null ? "Brush.state.paused" : words.Tone == ConfirmTone.Danger ? "Brush.state.danger" : "Brush.accent.primary");

        if (words.Action is null)
        {
            // A notice: one button, which Enter and Esc both press, filled since it is the only way on.
            Confirm.Visibility = Visibility.Collapsed;
            Cancel.IsDefault = true;
            if (FindResource("Button.Primary") is Style primary)
            {
                Cancel.Style = primary;
            }

            if (FindResource("Banner.Warning") is Style banner)
            {
                ConsequenceBox.Style = banner;
                ConsequenceBox.Margin = new Thickness(0, 12, 0, 0);
            }
        }
        else if (typed is not null)
        {
            TypedPart.Visibility = Visibility.Visible;
            Instruction.Text = typed.Prompt;
            Confirm.IsEnabled = false;

            // Enter acts once the phrase is right: a disabled default button does nothing before then.
            Confirm.IsDefault = true;
        }
        else
        {
            Cancel.IsDefault = words.CancelIsDefault;
            Confirm.IsDefault = !words.CancelIsDefault;
        }

        Loaded += (_, _) => FocusStart();
    }

    /// <summary>The typed gate with the service's phrase (the tray's Discard, Delete everything).</summary>
    public ConfirmWindow(ConfirmWords words, string phrase)
        : this(words, new TypedConfirmation(phrase))
    {
    }

    /// <summary>Shows a question and says whether the technician chose its action.</summary>
    public static bool Asks(Window? owner, ConfirmWords words, TypedConfirmation? typed = null) => Show(owner, new ConfirmWindow(words, typed));

    /// <summary>Shows a notice with its one button.</summary>
    public static void Tells(Window? owner, ConfirmWords words) => _ = Show(owner, new ConfirmWindow(words));

    /// <summary>Spec §5 S3's Discard, typed with DISCARD: the raw data goes now, and the audit row records that it did.</summary>
    public static bool AskToDiscard(Window? owner, TypedConfirmation confirmation) => Asks(owner, ConfirmCopy.DiscardSession, confirmation);

    /// <summary>
    /// Spec §5 S4's bulk discard: the technician types how many sessions go (v0.4.1 Q6). The number is the
    /// service's, which counted the ids it bound the token to.
    /// </summary>
    public static bool AskToDiscardSessions(Window? owner, TypedConfirmation confirmation, int count) =>
        Asks(owner, ConfirmCopy.DiscardSessions(count), confirmation);

    private static bool Show(Window? owner, ConfirmWindow dialog)
    {
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true;
    }

    private void FocusStart()
    {
        if (_typed is not null)
        {
            _ = Typed.Focus();
        }
        else if (_words.Action is null || _words.CancelIsDefault)
        {
            _ = Cancel.Focus();
        }
        else
        {
            _ = Confirm.Focus();
        }
    }

    private void OnTyped(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Confirm.IsEnabled = _typed?.Accepts(Typed.Text) ?? true;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        // Checked again rather than trusting the button: the button's enabled state is a view of the rule,
        // and a rule enforced only by what is clickable is enforced only until something else clicks it.
        if (_typed is null || _typed.Accepts(Typed.Text))
        {
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
