using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.Review;

/// <summary>
/// The note as it will land (ST-074, Spec v0.6.0 §5 S3): the session pane under the envelope.
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

        // What Ctrl+Z means after Del on a step's frame depends on whether anything was typed since.
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => _typedSinceDelete = true));
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is NoteEditorViewModel old)
            {
                old.PropertyChanged -= OnModelChanged;
            }

            if (e.NewValue is NoteEditorViewModel now)
            {
                now.PropertyChanged += OnModelChanged;
            }
        };

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

    /// <summary>
    /// `Space` on a step's frame includes or excludes it, as in the strip (Spec §5 S3: the strip's actions
    /// on the step's row). On PreviewKeyDown, so the button never sees the press and does not also enlarge
    /// it; Enter and a click are the button's own, through ShowFrameCommand.
    /// </summary>
    /// <remarks>
    /// `Del` deletes it, as in the strip (Spec v0.6.8 §5 S3), with the same five-second undo, which is on
    /// the note. The button goes with the frame, so focus moves into the step's text, at its end, where a
    /// second Del has nothing to delete; Ctrl+Z from there brings the frame back (<see cref="OnNoteKeyDown"/>).
    /// </remarks>
    private void OnFrameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FrameChip chip } || Model is not { } model)
        {
            return;
        }

        switch (NoteEditorKeys.RouteFrame(e.Key, e.SystemKey, e.KeyboardDevice.Modifiers))
        {
            case NoteFrameKey.ToggleInclude:
                if (!e.IsRepeat)
                {
                    model.ToggleFrameCommand.Execute(chip);
                }

                e.Handled = true;
                break;

            case NoteFrameKey.Delete:
                // A held Del deletes one screenshot, not every one the focus lands on.
                if (!e.IsRepeat)
                {
                    var row = model.Steps.FirstOrDefault(step => step.Frames.Contains(chip));
                    _deleted = chip;
                    model.DeleteFrameCommand.Execute(chip);
                    if (row is not null && !chip.Present)
                    {
                        Focus(row);
                    }
                }

                e.Handled = true;
                break;

            case NoteFrameKey.None:
            default:
                break;
        }
    }

    /// <summary>The step's frame Del last took out here, so the undo can put focus back on it.</summary>
    private FrameChip? _deleted;

    /// <summary>The note's text has changed since the last delete: Ctrl+Z in a text box is that box's own again.</summary>
    private bool _typedSinceDelete;

    /// <summary>
    /// `Ctrl+Z` anywhere in the note takes back a screenshot's delete while it is inside its five seconds
    /// (Spec v0.6.8 §5 S3), as it does in the strip. On PreviewKeyDown, ahead of the focused text box,
    /// whose own undo it stays once the technician has typed since Del (<see cref="Core.Review.NoteLanding.UndoIsTheDelete"/>).
    /// </summary>
    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model
            || !NoteEditorKeys.IsUndoOfTheDelete(e.Key, e.KeyboardDevice.Modifiers, model.HasUndoToast, Keyboard.FocusedElement is TextBox, _typedSinceDelete))
        {
            return;
        }

        var back = _deleted;
        _deleted = null;
        model.UndoDeleteCommand.Execute(null);
        e.Handled = true;
        if (back is not null)
        {
            FocusFrame(back);
        }
    }

    /// <summary>The toast's Undo, pressed: the frame is back, and focus goes to it rather than nowhere when the toast goes.</summary>
    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_deleted is { } back)
        {
            _deleted = null;
            FocusFrame(back);
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A new delete, here or in the strip: nothing has been typed since it.
        if (e.PropertyName == nameof(NoteEditorViewModel.UndoToast) && Model is { HasUndoToast: true })
        {
            _typedSinceDelete = false;
        }
    }

    /// <summary>Puts focus on a step's frame once it is back on its row and laid out.</summary>
    private void FocusFrame(FrameChip chip) =>
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (chip.Present && FindFrame(StepList, chip) is { IsVisible: true } frame)
                {
                    _ = frame.Focus();
                }
            });

    private static Button? FindFrame(DependencyObject parent, FrameChip chip)
    {
        var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < children; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && ReferenceEquals(button.DataContext, chip))
            {
                return button;
            }

            if (FindFrame(child, chip) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks right: the command confirms the step, and the button goes with the marker, so the caret is
    /// put in the step's text rather than left nowhere.
    /// </summary>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepRow row })
        {
            Focus(row);
        }
    }

    /// <summary>Edit: the caret goes into the step's text, at its end.</summary>
    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepRow row })
        {
            Focus(row);
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

/// <summary>What one keypress on a step's frame does.</summary>
public enum NoteFrameKey
{
    None,
    ToggleInclude,
    Delete,
}

/// <summary>The step box's chords (Spec §5 S3), as one pure function so they are tested without a window.</summary>
public static class NoteEditorKeys
{
    /// <summary>
    /// A step's frame: `Space` includes or excludes it and `Del` deletes it, as in the strip (Spec v0.6.8
    /// §5 S3). Bare keys only; Enter is the button's own, and enlarges it.
    /// </summary>
    /// <param name="key">WPF's <see cref="KeyEventArgs.Key"/>, which is <see cref="Key.System"/> under Alt.</param>
    /// <param name="systemKey">WPF's <see cref="KeyEventArgs.SystemKey"/>.</param>
    public static NoteFrameKey RouteFrame(Key key, Key systemKey, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None)
        {
            return NoteFrameKey.None;
        }

        return (key == Key.System ? systemKey : key) switch
        {
            Key.Space => NoteFrameKey.ToggleInclude,
            Key.Delete => NoteFrameKey.Delete,
            _ => NoteFrameKey.None,
        };
    }

    /// <summary>
    /// Whether this press is Ctrl+Z taking back a screenshot's delete: Ctrl+Z exactly (Ctrl+Shift+Z is a
    /// redo), while one is pending, and not the focused text box's own undo
    /// (<see cref="Core.Review.NoteLanding.UndoIsTheDelete"/>).
    /// </summary>
    public static bool IsUndoOfTheDelete(Key key, ModifierKeys modifiers, bool deletePending, bool inTextBox, bool typedSinceDelete) =>
        key == Key.Z
        && modifiers == ModifierKeys.Control
        && Core.Review.NoteLanding.UndoIsTheDelete(deletePending, inTextBox, typedSinceDelete);

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
