using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.Review;

/// <summary>
/// Layout only. Everything it shows is <see cref="ReviewViewModel"/>'s. A note's frame chip reaches the
/// filmstrip through the note's own ShowFrameCommand (T-165), so nothing here carries it.
///
/// The one key it handles is Spec §5 S3's Alt+1/2/3, which moves focus to the note, the screenshots or
/// the envelope (the receipt, once published): focus is the view's business, so it lives here (ST-084
/// found it unbound).
///
/// It also sets the session form's floor: the envelope and the timeline take what they need, the panes
/// get at least <c>layout.pane-min-height</c>, and the form is never shorter than the three together, so
/// on a short window it scrolls rather than squeezing the panes to nothing under an open timeline.
/// </summary>
public partial class ReviewPane : UserControl
{
    public ReviewPane()
    {
        InitializeComponent();
    }

    // Neither part's height depends on the form's (the envelope's on its width, the timeline's on its
    // own state), so setting the floor here settles in one more layout pass and never feeds back.
    private void OnFormPartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged)
        {
            return;
        }

        Form.MinHeight = Outer(EnvelopeCard) + PanesRow.MinHeight + Outer(TimelineRow);
    }

    private static double Outer(FrameworkElement part) => part.ActualHeight + part.Margin.Top + part.Margin.Bottom;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (PaneFor(e.Key == Key.System ? e.SystemKey : e.Key, e.KeyboardDevice.Modifiers) is { } pane)
        {
            e.Handled = pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            if (e.Handled)
            {
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    // The digit row only: Alt with the number pad types a character by its code (Alt+0233 is é), and a
    // technician typing one into a step must get the character, not a jump to another pane.
    private FrameworkElement? PaneFor(Key key, ModifierKeys modifiers) => modifiers != ModifierKeys.Alt ? null : key switch
    {
        Key.D1 => Note,
        Key.D2 => Strip,
        Key.D3 => Receipt.IsVisible ? (FrameworkElement)Receipt : Publish,
        _ => null,
    };
}
