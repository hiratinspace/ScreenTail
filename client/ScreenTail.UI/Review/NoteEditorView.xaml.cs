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
        Unloaded += (_, _) => _tick.Stop();
    }

    /// <summary>Raised when a frame chip is clicked, so the shell can jump the filmstrip (ST-075).</summary>
    public event EventHandler<string>? FrameRequested;

    private NoteEditorViewModel? Model => DataContext as NoteEditorViewModel;

    private void OnFrameChip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string frameId })
        {
            FrameRequested?.Invoke(this, frameId);
        }
    }

    private void OnStepKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not StepRow row || Model is not { } model)
        {
            return;
        }

        var alt = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (e.Key)
        {
            // Alt+C confirms; the marker clears and the footer count drops.
            case Key.C when alt:
                model.ConfirmStepCommand.Execute(row);
                e.Handled = true;
                break;

            case Key.Up when alt:
                Move(model, row, -1);
                e.Handled = true;
                break;

            case Key.Down when alt:
                Move(model, row, 1);
                e.Handled = true;
                break;

            // Enter at the end of a step starts the next one. Anywhere else it is a line break inside the
            // step, because a step is prose and sometimes prose has two lines.
            case Key.Enter when !alt && box.CaretIndex == box.Text.Length:
                Focus(model.AddStepAfter(row));
                e.Handled = true;
                break;

            // Backspace at the start of an empty step deletes it, which is what every list editor does.
            // The focus goes to the step above so the technician carries on typing where they were.
            case Key.Back when box.Text.Length == 0:
                var previous = PreviousOf(model, row);
                if (model.RemoveStep(row) && previous is { } target)
                {
                    Focus(target);
                }

                e.Handled = true;
                break;

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
