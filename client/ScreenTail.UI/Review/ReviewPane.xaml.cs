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

        // Handled events too: whatever took focus may have marked it, and the form still has to show it.
        FormScroll.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFormFocus), handledEventsToo: true);
    }

    /// <summary>
    /// Whatever takes keyboard focus in the form is scrolled fully into the form's view, focus ring and
    /// all. WPF's own bring-into-view stops at the nearest scroller (the transcript's, the note's), so an
    /// item those show could still sit below the form's fold: a transcript line Tab reached was drawn
    /// off the bottom of a short window.
    /// </summary>
    private void OnFormFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not FrameworkElement focused || !focused.IsDescendantOf(Form))
        {
            return;
        }

        focused.BringIntoView();
        FormScroll.UpdateLayout();
        if (!focused.IsDescendantOf(Form))
        {
            return;
        }

        var box = focused.TransformToAncestor(FormScroll).TransformBounds(new Rect(focused.RenderSize));
        box.Inflate(0, RingAllowance);
        var offset = FormScroll.VerticalOffset;
        if (box.Top < 0)
        {
            FormScroll.ScrollToVerticalOffset(offset + box.Top);
        }
        else if (box.Bottom > FormScroll.ViewportHeight)
        {
            // Bottom edge into view, but never so far that the top goes out of it.
            FormScroll.ScrollToVerticalOffset(offset + Math.Min(box.Top, box.Bottom - FormScroll.ViewportHeight));
        }
    }

    // The focus ring is drawn 3 px outside a control (Components.xaml's FocusRing); a little more, so the
    // ring is never cut by the fold.
    private const double RingAllowance = 8;

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
