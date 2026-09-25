using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using ScreenTail.Core.Review;

namespace ScreenTail.UI.Review;

/// <summary>
/// The centre pane (ST-075, Spec §5 S3). Routing only: what each chord does is a call into
/// <see cref="FilmstripViewModel"/>, and from there into <c>ScreenTail.Core.Review</c>.
/// </summary>
public partial class FilmstripView : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    {
        // The undo window is five seconds and the toast counts against it, so a quarter-second tick is the
        // difference between "5 s" meaning five and meaning up to six.
        Interval = TimeSpan.FromMilliseconds(250),
    };

    public FilmstripView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Model?.Tick();
        Loaded += async (_, _) =>
        {
            _tick.Start();
            if (Model is { } model)
            {
                await model.LoadImagesAsync();
            }
        };

        // Not merely stopping the timer: anything still inside an undo window is committed, because a
        // frame the technician deleted must not survive because they closed the pane quickly.
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            Model?.Dispose();
        };
    }

    private FilmstripViewModel? Model => DataContext as FilmstripViewModel;

    private System.Windows.Point? _blurFrom;

    /// <summary>
    /// `B`, and the toggle. Arming is a separate act from dragging, because the pane is full of frames a
    /// stray drag would otherwise destroy a piece of.
    /// </summary>
    private void OnBlurModeChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        BlurSurface.Visibility = BlurMode.IsChecked == true
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        BlurRect.Visibility = System.Windows.Visibility.Collapsed;
        _blurFrom = null;
    }

    private void OnBlurDown(object sender, MouseButtonEventArgs e)
    {
        _blurFrom = e.GetPosition(EnlargedImage);
        BlurRect.Visibility = System.Windows.Visibility.Collapsed;
        BlurSurface.CaptureMouse();
    }

    private void OnBlurMove(object sender, MouseEventArgs e)
    {
        if (_blurFrom is not { } from)
        {
            return;
        }

        var to = e.GetPosition(EnlargedImage);
        var rect = Rect.Between(from.X, from.Y, to.X, to.Y);

        Canvas.SetLeft(BlurRect, rect.Left);
        Canvas.SetTop(BlurRect, rect.Top);
        BlurRect.Width = rect.Width;
        BlurRect.Height = rect.Height;
        BlurRect.Visibility = System.Windows.Visibility.Visible;
    }

    private async void OnBlurUp(object sender, MouseButtonEventArgs e)
    {
        BlurSurface.ReleaseMouseCapture();
        BlurRect.Visibility = System.Windows.Visibility.Collapsed;
        if (_blurFrom is not { } from || Model is not { Enlarged: { } frame } model)
        {
            return;
        }

        var to = e.GetPosition(EnlargedImage);
        _blurFrom = null;

        // Measured against the Image, not the Canvas: Stretch="Uniform" letterboxes, so the Canvas is
        // larger than the picture and a rectangle in its coordinates would be scaled from the wrong size.
        await model.BlurAsync(
            frame,
            Rect.Between(from.X, from.Y, to.X, to.Y),
            new Size(EnlargedImage.ActualWidth, EnlargedImage.ActualHeight));

        // Disarmed after each one. A mode that stays on is a mode somebody forgets is on, and every
        // subsequent drag over this pane is irreversible.
        BlurMode.IsChecked = false;
    }

    /// <summary>A selection made from outside — a transcript line, a frame chip — is scrolled into view.</summary>
    private void OnStripSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Strip.SelectedItem is { } selected)
        {
            Strip.ScrollIntoView(selected);
        }
    }

    private void OnStripDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is { } model && Strip.SelectedItem is FrameItem frame)
        {
            model.EnlargeCommand.Execute(frame);
        }
    }

    private void OnStripKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        // Esc closes the enlarged view whatever is selected, and takes priority: it is the way out.
        if (e.Key == Key.Escape && model.IsEnlarged)
        {
            model.CloseEnlargedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // T is the OCR toggle and only means anything over an enlarged frame. Everywhere else it is a
        // letter, and a letter that silently did something would be a trap once this pane gets a search box.
        if (e.Key == Key.T && model.IsEnlarged)
        {
            model.ToggleOcrTextCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Strip.SelectedItem is not FrameItem frame)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                model.ToggleIncludeCommand.Execute(frame);
                e.Handled = true;
                break;

            case Key.Enter:
                model.EnlargeCommand.Execute(frame);
                e.Handled = true;
                break;

            case Key.Delete:
                model.DeleteCommand.Execute(frame);
                e.Handled = true;
                break;

            case Key.B when model.IsEnlarged:
                BlurMode.IsChecked = BlurMode.IsChecked != true;
                e.Handled = true;
                break;

            // Ctrl+Z as well as the toast's button: the toast is reachable by keyboard, but nobody tabs to
            // a control that is about to disappear.
            case Key.Z when e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control):
                model.UndoCommand.Execute(null);
                e.Handled = true;
                break;

            default:
                break;
        }
    }
}
