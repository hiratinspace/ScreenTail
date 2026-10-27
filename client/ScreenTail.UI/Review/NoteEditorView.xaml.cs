using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.Review;

/// <summary>
/// The note pane (ST-074, Spec §5 S3 left pane).
///
/// Only what needs a window lives here: routing the chords to the focused step, and putting the caret in
/// a step that has just been created or is about to disappear. Everything the chords do is a call into
/// <see cref="NoteEditorViewModel"/>, and from there into <c>ScreenTail.Core.Review</c>, so the rules are
/// tested without WPF (ADR-0002) and this file has nothing in it to get wrong twice.
/// </summary>
public partial class NoteEditorView : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    {
        // Four times a second. The autosave decides whether a write is due; this only has to arrive often
        // enough that its 900 ms ceiling is not really 900 ms plus a tick.
        Interval = TimeSpan.FromMilliseconds(250),
    };

    public NoteEditorView()
    {
        InitializeComponent();

        // Without this nothing ever calls TickAsync, and the autosave's whole debounce-and-ceiling
        // argument would be code with no caller: edits would reach disk only on Ctrl+S or on close.
        _tick.Tick += async (_, _) =>
        {
            if (Model is { } model)
            {
                await model.TickAsync();
            }
        };

        Loaded += (_, _) => _tick.Start();

        // T-169: the autosave waits for a pause in typing, so leaving Review straight after typing used to
        // lose that much. Navigating away unloads the pane, and what is outstanding is written now.
        Unloaded += async (_, _) =>
        {
            _tick.Stop();
            if (Model is { } model)
            {
                await model.FlushAsync();
            }
        };
    }

    private NoteEditorViewModel? Model => DataContext as NoteEditorViewModel;

    /// <summary>
    /// On PreviewKeyDown (T-168): a multiline TextBox runs Enter and Backspace as editing commands and
    /// marks them handled before a KeyDown handler on it sees them, so Enter never started a step.
    /// </summary>
    private void OnStepKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not StepRow row || Model is not { } model)
        {
            return;
        }

        switch (NoteEditorKeys.Route(e.Key, e.SystemKey, e.KeyboardDevice.Modifiers, box.CaretIndex == box.Text.Length, box.Text.Length == 0))
        {
            // Alt+C confirms; the marker clears and the footer count drops.
            case NoteEditorKey.ConfirmStep:
                model.ConfirmStepCommand.Execute(row);
                e.Handled = true;
                break;

            case NoteEditorKey.MoveUp:
                Move(model, row, -1);
                e.Handled = true;
                break;

            case NoteEditorKey.MoveDown:
                Move(model, row, 1);
                e.Handled = true;
                break;

            case NoteEditorKey.AddStep:
                Focus(model.AddStepAfter(row));
                e.Handled = true;
                break;

            // The focus goes to the step above so the technician carries on typing where they were.
            case NoteEditorKey.RemoveStep:
                var previous = PreviousOf(model, row);
                if (model.RemoveStep(row) && previous is { } target)
                {
                    Focus(target);
                }

                e.Handled = true;
                break;

            case NoteEditorKey.None:
            default:
                break;
        }
    }

    private static StepRow? PreviousOf(NoteEditorViewModel model, StepRow row)
    {
        var at = model.Steps.IndexOf(row);
        return at > 0 ? model.Steps[at - 1] : null;
    }

    private void Move(NoteEditorViewModel model, StepRow row, int delta)
    {
        model.MoveStepCommand.Execute((row, delta));

        // The container is rebuilt by the move, so the focus has to be put back or an Alt+↓ that worked
        // leaves the caret nowhere and the next Alt+↓ does nothing.
        Focus(row);
    }

    private void Focus(StepRow row)
    {
        // After the item is realised: asking for its container in the same turn returns null, and the
        // caret silently stays where it was.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (StepList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
                    && FindTextBox(container) is { } box)
                {
                    box.Focus();
                    box.CaretIndex = box.Text.Length;
                }
            });
    }

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < children; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox box)
            {
                return box;
            }

            if (FindTextBox(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>What one keypress in a step box does.</summary>
public enum NoteEditorKey
{
    None,
    ConfirmStep,
    MoveUp,
    MoveDown,
    AddStep,
    RemoveStep,
}

/// <summary>The step box's chords (Spec §5 S3), as one pure function so they are tested without a window.</summary>
public static class NoteEditorKeys
{
    /// <param name="key">WPF's <see cref="KeyEventArgs.Key"/>, which is <see cref="Key.System"/> under Alt (T-240).</param>
    /// <param name="systemKey">WPF's <see cref="KeyEventArgs.SystemKey"/>: the real key when <paramref name="key"/> is <see cref="Key.System"/>.</param>
    /// <param name="caretAtEnd">Whether the caret is after the step's last character.</param>
    /// <param name="empty">Whether the step has no text.</param>
    public static NoteEditorKey Route(Key key, Key systemKey, ModifierKeys modifiers, bool caretAtEnd, bool empty)
    {
        var pressed = key == Key.System ? systemKey : key;
        if (modifiers == ModifierKeys.Alt)
        {
            return pressed switch
            {
                Key.C => NoteEditorKey.ConfirmStep,
                Key.Up => NoteEditorKey.MoveUp,
                Key.Down => NoteEditorKey.MoveDown,
                _ => NoteEditorKey.None,
            };
        }

        if (modifiers != ModifierKeys.None)
        {
            // Shift+Enter is a line break the box keeps, and Ctrl+Enter is Publish, which the pane's binding
            // must still receive.
            return NoteEditorKey.None;
        }

        return pressed switch
        {
            // Enter at the end of a step starts the next one. Anywhere else it is a line break inside the
            // step, because a step is prose and sometimes prose has two lines.
            Key.Enter when caretAtEnd => NoteEditorKey.AddStep,

            // Backspace in an empty step deletes it, which is what every list editor does.
            Key.Back when empty => NoteEditorKey.RemoveStep,
            _ => NoteEditorKey.None,
        };
    }
}
