using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenTail.UI.Review;

/// <summary>
/// Layout only. Everything it shows is <see cref="ReviewViewModel"/>'s. A step's frame reaches the
/// filmstrip through the note's own ShowFrameCommand (T-165), so nothing here carries it.
///
/// The keys it handles are Spec §5 S3's Alt+1/2/3, which move focus to the note, the screenshots or the
/// envelope (the receipt, once published): focus is the view's business, so it lives here (ST-084 found
/// it unbound). Since the note carries each step's frame (Spec v0.6.0 §5 S3), the whole strip opens over
/// the note: Alt+2 opens it, Alt+1, Esc and "Back to the note" put it away, and focus goes back where it
/// was in the note.
///
/// It also sets the session form's floor: the envelope and the timeline take what they need, the note
/// gets at least <c>layout.pane-min-height</c>, and the form is never shorter than the three together, so
/// on a short window it scrolls rather than squeezing the note to nothing under an open timeline.
/// </summary>
public partial class ReviewPane : UserControl
{
    public ReviewPane()
    {
        InitializeComponent();

        // Handled events too: whatever took focus may have marked it, and the form still has to show it.
        FormScroll.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFormFocus), handledEventsToo: true);
        NotePane.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnNoteFocus), handledEventsToo: true);
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

        SetFormFloor();
    }

    private void SetFormFloor() => Form.MinHeight = Outer(EnvelopeCard) + PanesRow.MinHeight + Outer(TimelineRow);

    // The strip, and a frame enlarged in it, need more than the note's floor. Beside the rail at the
    // smallest window the envelope wraps, the panes sit on their 300 px floor, and under the way back, the
    // frame's heading, its buttons and the blur hint an enlarged frame was left 63 px: drawn smaller than
    // its own thumbnail, with a blur box too small to show its focus rings. So while the strip is over the
    // note the floor is layout.strip-min-height, and the form scrolls where the window cannot hold it.
    private void SetPanesFloor(bool stripShown)
    {
        PanesRow.SetResourceReference(RowDefinition.MinHeightProperty, stripShown ? "Layout.strip-min-height" : "Layout.pane-min-height");
        SetFormFloor();
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
    // technician typing one into a step must get the character, not a jump to another pane. Alt+1 puts
    // the strip away if it is over the note, and Alt+2 opens it there, before focus goes in.
    private FrameworkElement? PaneFor(Key key, ModifierKeys modifiers) => modifiers != ModifierKeys.Alt ? null : key switch
    {
        Key.D1 => ShowNote(),
        Key.D2 => ShowStrip(),
        Key.D3 => Receipt.IsVisible ? (FrameworkElement)Receipt : Publish,
        _ => null,
    };

    private FilmstripViewModel? StripModel => (DataContext as ReviewViewModel)?.Strip;

    /// <summary>The note, with the strip and any enlarged frame put away first, laid out so focus can go in.</summary>
    private NoteEditorView ShowNote()
    {
        if (StripModel is { } strip)
        {
            if (strip.IsEnlarged)
            {
                strip.CloseEnlargedCommand.Execute(null);
            }

            strip.Browsing = false;
            UpdateLayout();
        }

        return Note;
    }

    /// <summary>The whole strip, over the note, laid out so focus can go in.</summary>
    private FilmstripView ShowStrip()
    {
        if (StripModel is { Browsing: false } strip)
        {
            strip.Browsing = true;
            UpdateLayout();
        }

        return Strip;
    }

    /// <summary>
    /// The last control in the note that had keyboard focus, so going back from the strip or an enlarged
    /// frame puts it where it was: on the step's frame that was opened, or on "All screenshots".
    /// </summary>
    private IInputElement? _inNote;

    private void OnNoteFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is Visual visual && visual.IsDescendantOf(NotePane))
        {
            _inNote = e.NewFocus;
        }
    }

    /// <summary>
    /// The strip coming over the note or going. Opened to browse, focus goes into it (Alt+2 has put it
    /// there already); opened by a step's frame, the enlarged view puts it on Close itself. Gone, focus
    /// goes back where it was in the note, after the strip's own handler has had its turn.
    /// </summary>
    private void OnStripShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        var open = e.NewValue is true;
        SetPanesFloor(open);
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
            {
                if (open)
                {
                    if (StripModel is { IsEnlarged: false } && !StripPane.IsKeyboardFocusWithin)
                    {
                        _ = Strip.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    }

                    return;
                }

                if (Keyboard.FocusedElement is not UIElement { IsVisible: true } || StripPane.IsKeyboardFocusWithin)
                {
                    if (_inNote is UIElement { IsVisible: true, Focusable: true } back)
                    {
                        _ = back.Focus();
                    }
                    else
                    {
                        _ = Note.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    }
                }
            });
    }

    /// <summary>"Back to the note": the strip goes, and focus returns to where it was in the note.</summary>
    private void OnBackToNote(object sender, RoutedEventArgs e)
    {
        if (StripModel is { } strip)
        {
            strip.Browsing = false;
        }
    }

    /// <summary>
    /// Esc in the strip, with no frame enlarged, is back to the note. The enlarged view and the blur box
    /// answer Esc first and mark it handled, so it never reaches here while either is out.
    /// </summary>
    private void OnStripPaneKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.None && StripModel is { IsEnlarged: false, Browsing: true } strip)
        {
            strip.Browsing = false;
            e.Handled = true;
        }
    }
}
