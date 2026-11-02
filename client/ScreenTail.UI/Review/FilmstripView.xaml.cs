using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using ScreenTail.Core.Review;

namespace ScreenTail.UI.Review;

/// <summary>
/// The centre pane (ST-075, Spec §5 S3). Routing only: what each chord does is a call into
/// <see cref="FilmstripViewModel"/>, and from there into <c>ScreenTail.Core.Review</c>.
/// </summary>
#pragma warning disable CA1001 // _showing is cancelled and disposed on every Unloaded, which is a control's end of life; a control has no Dispose to call.
public partial class FilmstripView : UserControl
#pragma warning restore CA1001
{
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    {
        // The undo window is five seconds and the toast counts against it, so a quarter-second tick is the
        // difference between "5 s" meaning five and meaning up to six.
        Interval = TimeSpan.FromMilliseconds(250),
    };

    /// <summary>The thumbnail load's lifetime: one per Loaded, cancelled on Unloaded (E-105).</summary>
    private CancellationTokenSource? _showing;

    public FilmstripView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Model?.Tick();
        Loaded += async (_, _) =>
        {
            _tick.Start();
            if (Model is not { } model)
            {
                return;
            }

            StopLoading();
            _showing = new CancellationTokenSource();
            try
            {
                await model.LoadImagesAsync(_showing.Token);
            }
            catch (OperationCanceledException)
            {
                // The pane went before every thumbnail arrived. Nothing is owed to a view that is gone.
            }
        };

        // Not merely stopping the timer: the thumbnail load stops pulling pictures over the shared pipe,
        // and anything still inside an undo window is committed, because a frame the technician deleted
        // must not survive because they closed the pane quickly.
        Unloaded += (_, _) =>
        {
            _tick.Stop();
            StopLoading();
            Model?.Dispose();
        };
    }

    private void StopLoading()
    {
        if (_showing is { } showing)
        {
            _showing = null;
            showing.Cancel();
            showing.Dispose();
        }
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

    /// <summary>
    /// The enlarged view opening or closing (ST-084). Open, it covers the strip, its header and its footer,
    /// so they are hidden under it (keeping their place) and focus moves to Close: Tab could otherwise walk
    /// the keyboard through thumbnails and an Undo nobody can see. Closed, they come back and focus returns
    /// to the screenshot that was enlarged.
    /// </summary>
    private void OnEnlargedVisibility(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        var open = e.NewValue is true;
        var covered = open ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
        Strip.Visibility = covered;
        StripHeader.Visibility = covered;
        StripFooter.Visibility = covered;

        // After layout, so the control being focused is visible and its container exists.
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (open)
                {
                    _ = CloseEnlarged.Focus();
                }
                else if (IsKeyboardFocusWithin || Keyboard.FocusedElement is null)
                {
                    var container = Strip.SelectedItem is { } selected ? Strip.ItemContainerGenerator.ContainerFromItem(selected) as System.Windows.UIElement : null;
                    _ = (container ?? Strip).Focus();
                }
            });
    }

    /// <summary>Esc, T, B and Ctrl+Z from any control of the enlarged view, not only from the strip under it (ST-084).</summary>
    private void OnEnlargedKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (FilmstripKeys.Route(key, e.KeyboardDevice.Modifiers, model.IsEnlarged, frameSelected: true))
        {
            case FilmstripKey.CloseEnlarged:
                model.CloseEnlargedCommand.Execute(null);
                e.Handled = true;
                break;
            case FilmstripKey.ToggleOcrText:
                model.ToggleOcrTextCommand.Execute(null);
                e.Handled = true;
                break;
            case FilmstripKey.ToggleBlur:
                BlurMode.IsChecked = BlurMode.IsChecked != true;
                e.Handled = true;
                break;

            // The undo toast is under the enlarged view, but its five seconds run on: Ctrl+Z still takes
            // the delete back from here (ST-084 review).
            case FilmstripKey.Undo:
                model.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            default:
                // Space, Enter and Del belong to the button that has focus here, not to the strip.
                break;
        }
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

    /// <summary>On PreviewKeyDown, so the ListBox's own handling of Space cannot swallow it (T-167).</summary>
    private void OnStripKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is { } model)
        {
            FilmstripKeys.Handle(model, Strip.SelectedItem as FrameItem, e, () => BlurMode.IsChecked = BlurMode.IsChecked != true);
        }
    }
}

/// <summary>What one keypress in the strip does.</summary>
public enum FilmstripKey
{
    None,
    CloseEnlarged,
    ToggleOcrText,
    ToggleInclude,
    Enlarge,
    Delete,
    ToggleBlur,
    Undo,
}

/// <summary>
/// The strip's chords (Spec §5 S3), as one pure routing function and the one place that acts on it, so
/// the rules are tested without a window and the view only says which frame is selected.
/// </summary>
public static class FilmstripKeys
{
    /// <param name="key">The key, already read through <see cref="KeyEventArgs.SystemKey"/> when WPF reports <see cref="Key.System"/>.</param>
    public static FilmstripKey Route(Key key, ModifierKeys modifiers, bool enlarged, bool frameSelected)
    {
        // Esc closes the enlarged view whatever is selected, and takes priority: it is the way out.
        if (key == Key.Escape)
        {
            return enlarged ? FilmstripKey.CloseEnlarged : FilmstripKey.None;
        }

        // Ctrl+Z as well as the toast's button: the toast is reachable by keyboard, but nobody tabs to a
        // control that is about to disappear. Not tied to a selection, because the delete it takes back
        // has just removed the selected frame.
        if (key == Key.Z)
        {
            return modifiers == ModifierKeys.Control ? FilmstripKey.Undo : FilmstripKey.None;
        }

        // Every other chord is a bare key. Ctrl+Enter in particular is Publish (Spec §5 S3) and must reach
        // the pane's binding rather than enlarge a frame.
        if (modifiers != ModifierKeys.None)
        {
            return FilmstripKey.None;
        }

        // T is the OCR toggle and only means anything over an enlarged frame. Everywhere else it is a
        // letter, and a letter that silently did something would be a trap once this pane gets a search box.
        if (key == Key.T)
        {
            return enlarged ? FilmstripKey.ToggleOcrText : FilmstripKey.None;
        }

        if (!frameSelected)
        {
            return FilmstripKey.None;
        }

        return key switch
        {
            Key.Space => FilmstripKey.ToggleInclude,
            Key.Enter => FilmstripKey.Enlarge,
            Key.Delete => FilmstripKey.Delete,
            Key.B when enlarged => FilmstripKey.ToggleBlur,
            _ => FilmstripKey.None,
        };
    }

    /// <summary>Routes the keypress and does what it says, marking it handled when it did anything.</summary>
    /// <param name="toggleBlur">Arms or disarms the blur surface, which belongs to the view.</param>
    public static void Handle(FilmstripViewModel model, FrameItem? selected, KeyEventArgs e, Action toggleBlur)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(toggleBlur);

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var route = Route(key, e.KeyboardDevice.Modifiers, model.IsEnlarged, selected is not null);
        switch (route)
        {
            case FilmstripKey.CloseEnlarged:
                model.CloseEnlargedCommand.Execute(null);
                break;
            case FilmstripKey.ToggleOcrText:
                model.ToggleOcrTextCommand.Execute(null);
                break;
            case FilmstripKey.ToggleInclude:
                model.ToggleIncludeCommand.Execute(selected);
                break;
            case FilmstripKey.Enlarge:
                model.EnlargeCommand.Execute(selected);
                break;
            case FilmstripKey.Delete:
                model.DeleteCommand.Execute(selected);
                break;
            case FilmstripKey.ToggleBlur:
                toggleBlur();
                break;
            case FilmstripKey.Undo:
                model.UndoCommand.Execute(null);
                break;
            case FilmstripKey.None:
            default:
                return;
        }

        e.Handled = true;
    }
}
