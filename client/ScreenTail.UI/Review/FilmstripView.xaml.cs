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

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is FilmstripViewModel old)
            {
                old.PropertyChanged -= OnModelChanged;
            }

            if (e.NewValue is FilmstripViewModel now)
            {
                now.PropertyChanged += OnModelChanged;
            }

            if (e.OldValue is FilmstripViewModel was)
            {
                was.BlurAnswered -= OnBlurAnswered;
            }

            if (e.NewValue is FilmstripViewModel model)
            {
                model.BlurAnswered += OnBlurAnswered;
            }
        };
    }

    /// <summary>
    /// A blur the pane stopped waiting for has its answer (review of #257). The technician was last told it
    /// was still waiting, so what it came to is said, when the enlarged view is there to say it.
    /// </summary>
    private void OnBlurAnswered(FrameItem item, BlurOutcome outcome)
    {
        if (!EnlargedView.IsVisible)
        {
            return;
        }

        Say(outcome switch
        {
            BlurOutcome.Blurred => "The last blur has now been applied.",
            BlurOutcome.BlurredNotShown => "The last blur has now been applied. The screenshot is now too large to show here; its thumbnail shows the blur.",
            BlurOutcome.NotBlurred => "The last blur was not applied.",
            _ => MayNotHaveBeenApplied,
        });
    }

    private const string MayNotHaveBeenApplied = "The blur may not have been applied: the capture service went away. The screenshot is shown again as the service has it.";

    private const string StillWaiting = "Still waiting for the last blur.";

    /// <summary>
    /// The OCR text panel covers the picture, and the blur box with it. A box nobody can see must not be
    /// one Enter applies, so showing the text puts the box away (ST-084 follow-up review).
    /// </summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilmstripViewModel.ShowOcrText) && Model is { ShowOcrText: true } && BlurMode.IsChecked == true)
        {
            BlurMode.IsChecked = false;
        }
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

    /// <summary>Where the keyboard's blur box is, while it is out (ST-084 follow-up).</summary>
    private BlurBox _box = BlurBox.Centred;

    /// <summary>A blur on its way to the service. Nothing else is applied until it has an answer.</summary>
    private bool _blurring;

    /// <summary>
    /// How long a blur waits for the service before the pane says it did not answer. The pipe has no
    /// timeout of its own, and a pane waiting for ever cannot blur anything else (ST-084 follow-up review).
    /// </summary>
    internal static TimeSpan BlurWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// `B`, and the toggle. Arming is a separate act from dragging, because the pane is full of frames a
    /// stray drag would otherwise destroy a piece of.
    ///
    /// Armed, the keyboard's box comes out in the middle of the picture with focus on it, so a keyboard
    /// user can place it and a mouse user can still drag; the OCR text panel, which would cover it, closes.
    /// Disarmed, it goes, and focus that was on it goes back to the toggle rather than to nowhere.
    /// </summary>
    private void OnBlurModeChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        var armed = BlurMode.IsChecked == true;
        var hadFocus = KeyboardBox.IsKeyboardFocusWithin;
        var shown = armed ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        BlurSurface.Visibility = shown;
        BlurRect.Visibility = System.Windows.Visibility.Collapsed;
        _blurFrom = null;
        _box = BlurBox.Centred;
        KeyboardBox.Visibility = shown;
        if (armed)
        {
            if (Model is { ShowOcrText: true } model)
            {
                model.ShowOcrText = false;
            }

            // The words first, then shown: appearing is announced, and must announce these.
            ShowBox();
            BlurBoxStatus.Text = _box.Spoken;
            BlurBoxStatus.Visibility = System.Windows.Visibility.Visible;
            FocusBoxSoon();
            return;
        }

        BlurBoxStatus.Visibility = System.Windows.Visibility.Collapsed;
        if ((hadFocus || Keyboard.FocusedElement is null or System.Windows.Window) && EnlargedView.IsVisible)
        {
            _ = BlurMode.Focus();
        }
    }

    /// <summary>Focus on the box after layout, so it is visible and placed when it takes it.</summary>
    private void FocusBoxSoon() =>
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                if (BlurMode.IsChecked == true && KeyboardBox.IsVisible)
                {
                    _ = KeyboardBox.Focus();
                }
            });

    /// <summary>The picture or the surface over it changed size: the box stays over the same part of the picture.</summary>
    private void OnBlurLayoutChanged(object sender, System.Windows.SizeChangedEventArgs e) => ShowBox();

    /// <summary>The size the picture is drawn at, or null before it has been.</summary>
    private Size? Displayed() =>
        EnlargedImage.ActualWidth > 0 && EnlargedImage.ActualHeight > 0
            ? new Size(EnlargedImage.ActualWidth, EnlargedImage.ActualHeight)
            : null;

    /// <summary>
    /// Draws <paramref name="element"/> over <paramref name="drawn"/>, a rectangle in the picture's own
    /// coordinates. Stretch="Uniform" letterboxes the picture in the cell the surface fills, so it is
    /// placed that far across and down. The box and the drag's rectangle both go through here; the drag's
    /// was once drawn without the offset, off to one side of the pointer (what was blurred was right).
    /// </summary>
    private void Place(System.Windows.FrameworkElement element, Rect drawn)
    {
        var origin = EnlargedImage.TranslatePoint(default, BlurSurface);
        Canvas.SetLeft(element, origin.X + drawn.Left);
        Canvas.SetTop(element, origin.Y + drawn.Top);
        element.Width = drawn.Width;
        element.Height = drawn.Height;
    }

    /// <summary>Draws the keyboard's box where <see cref="_box"/> says. Saying where is the caller's.</summary>
    private void ShowBox()
    {
        if (KeyboardBox.Visibility != System.Windows.Visibility.Visible)
        {
            return;
        }

        if (Displayed() is { } displayed)
        {
            Place(KeyboardBox, _box.Drawn(displayed));
        }
    }

    /// <summary>
    /// Says <paramref name="words"/> through the status line: set first, then shown, and announced again
    /// when they are the words it already showed (a second failure, a second arrow into the edge).
    /// </summary>
    private void Say(string words)
    {
        var again = BlurBoxStatus.IsVisible && BlurBoxStatus.Text == words;
        BlurBoxStatus.Text = words;
        BlurBoxStatus.Visibility = System.Windows.Visibility.Visible;
        if (again)
        {
            Controls.LiveRegion.Announce(BlurBoxStatus);
        }
    }

    /// <summary>
    /// The press, read where it happened, and only then the capture. The order matters: WPF reads a
    /// position from the last pointer report, and capturing a window the pointer has not been reported over
    /// reports the real pointer as a move. Every real press carries its own position, so the two agree; the
    /// drag itself is <see cref="BeginDrag"/> and <see cref="EndDragAsync"/>, which take the points.
    /// </summary>
    private void OnBlurDown(object sender, MouseButtonEventArgs e)
    {
        BeginDrag(e.GetPosition(EnlargedImage));
        BlurSurface.CaptureMouse();
    }

    private void OnBlurMove(object sender, MouseEventArgs e)
    {
        if (_blurFrom is not { } from)
        {
            return;
        }

        var to = e.GetPosition(EnlargedImage);
        Place(BlurRect, Rect.Between(from.X, from.Y, to.X, to.Y));
        BlurRect.Visibility = System.Windows.Visibility.Visible;
    }

    private async void OnBlurUp(object sender, MouseButtonEventArgs e)
    {
        BlurSurface.ReleaseMouseCapture();
        await EndDragAsync(e.GetPosition(EnlargedImage));
    }

    /// <summary>A drag starts at <paramref name="at"/>, in the picture's own coordinates as drawn.</summary>
    internal void BeginDrag(System.Windows.Point at)
    {
        // A drag replaces the box: one rectangle on screen, the one that will be blurred.
        KeyboardBox.Visibility = System.Windows.Visibility.Collapsed;
        _blurFrom = at;
        BlurRect.Visibility = System.Windows.Visibility.Collapsed;
    }

    /// <summary>
    /// The drag ends at <paramref name="at"/>, in the picture's coordinates: the rectangle between the two
    /// points is blurred, unless it is too small to be anything but a click.
    /// </summary>
    internal async Task EndDragAsync(System.Windows.Point at)
    {
        BlurRect.Visibility = System.Windows.Visibility.Collapsed;
        if (_blurFrom is not { } from)
        {
            return;
        }

        _blurFrom = null;

        // Measured against the Image, not the Canvas: Stretch="Uniform" letterboxes, so the Canvas is
        // larger than the picture and a rectangle in its coordinates would be scaled from the wrong size.
        var drawn = Rect.Between(from.X, from.Y, at.X, at.Y);

        // A click, or a wobble: no attempt at all. Still armed for a real drag, nothing said, and no
        // keyboard box brought out for a mouse user (review of #257).
        if (!BlurRegion.IsMeaningful(drawn))
        {
            return;
        }

        await BlurAsync(drawn, fromKeyboard: false);
    }

    /// <summary>Enter on the box: the blur a drag over the same rectangle makes.</summary>
    private async void BlurUnderTheBox()
    {
        if (Displayed() is { } displayed)
        {
            await BlurAsync(_box.Drawn(displayed), fromKeyboard: true);
        }
        else if (Model?.Enlarged is { BlurPending: true })
        {
            Say(StillWaiting);
        }
    }

    /// <summary>
    /// The one way a blur is applied, from the mouse and the keyboard alike: the rectangle over the picture
    /// as drawn, and the size it is drawn at, go to <see cref="FilmstripViewModel.BlurAsync"/>, which scales
    /// it to the frame, has the service paint it with its margin, and drops the frame's text with it.
    ///
    /// What came of it is said, in fixed words (INV-10). Blurred, the box is put away. Not blurred, it stays
    /// where it was, so the technician can try again or press Esc: a failure that silently threw the box
    /// away would leave them believing the region was covered (ST-084 follow-up review).
    /// </summary>
    /// <param name="fromKeyboard">Enter on the box, rather than a drag: only then is a retry the box's to offer.</param>
    private async Task BlurAsync(Rect drawn, bool fromKeyboard)
    {
        // One blur at a time, and none of a frame whose last blur has no answer yet: said, not ignored.
        if (_blurring || Model?.Enlarged is { BlurPending: true })
        {
            Say(StillWaiting);
            return;
        }

        if (Model is not { Enlarged: { } frame } model || Displayed() is not { } displayed)
        {
            BlurMode.IsChecked = false;
            return;
        }

        _blurring = true;
        BlurOutcome? outcome = null;
        using var wait = new CancellationTokenSource(BlurWait);
        try
        {
            outcome = await model.BlurAsync(frame, drawn, displayed, wait.Token);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            // No answer in time. The view model goes on waiting for it, holds Publish back, and has stopped
            // showing the picture and its text, which may be from before; BlurAnswered says the answer.
        }
        finally
        {
            _blurring = false;
        }

        switch (outcome)
        {
            case BlurOutcome.Blurred:
                // Disarmed after each one. A mode that stays on is a mode somebody forgets is on, and every
                // subsequent drag over this pane is irreversible.
                BlurMode.IsChecked = false;
                Say("Region blurred");
                break;
            case BlurOutcome.BlurredNotShown:
                BlurMode.IsChecked = false;
                Say("Region blurred. The screenshot is now too large to show here; its thumbnail shows the blur.");
                break;
            case BlurOutcome.NotBlurred when fromKeyboard && BlurMode.IsChecked == true:
                KeepTheBoxOut();
                Say("Nothing was blurred. Press Enter to try again, or Esc to put the box away.");
                break;
            case BlurOutcome.NotBlurred:
                // A drag, or a box put away with Esc while it waited: still armed for a drag where it was,
                // and the words say only what happened.
                Say("Nothing was blurred.");
                break;
            case BlurOutcome.Unknown:
                BlurMode.IsChecked = false;
                Say(MayNotHaveBeenApplied);
                break;
            default:
                BlurMode.IsChecked = false;
                Say("The capture service has not answered yet. The screenshot is hidden and Publish waits until it does.");
                break;
        }
    }

    /// <summary>After a blur that did not land, the box is back where it was (a drag had hidden it), still armed.</summary>
    private void KeepTheBoxOut()
    {
        if (BlurMode.IsChecked != true)
        {
            // Put away with Esc while the service was asked: the technician has moved on.
            return;
        }

        KeyboardBox.Visibility = System.Windows.Visibility.Visible;
        ShowBox();
        if (Keyboard.FocusedElement is null or System.Windows.Window)
        {
            FocusBoxSoon();
        }
    }

    /// <summary>
    /// The keys the box answers while it is out (ST-084 follow-up). Esc puts it away from anywhere in the
    /// enlarged view, ahead of closing the frame, and while a blur is on its way too (its answer is still
    /// said). The arrows and Enter only while the box has focus, so on the buttons they still move between
    /// and press them. Ctrl+Enter is held back: publishing with a box placed and not applied would send the
    /// picture the technician was about to cover. Returns whether the key was the box's.
    /// </summary>
    private bool OnBoxKey(Key key, ModifierKeys modifiers)
    {
        if (BlurMode.IsChecked != true)
        {
            return false;
        }

        var press = BlurBoxKeys.Route(key, modifiers);
        switch (press.Kind)
        {
            case BlurBoxKey.Cancel:
                BlurMode.IsChecked = false;
                return true;
            case BlurBoxKey.HoldPublish:
                Say("Press Enter to blur or Esc to put the box away, then publish.");
                return true;
            case BlurBoxKey.Move when KeyboardBox.IsKeyboardFocused:
                if (!_blurring)
                {
                    var moved = _box.Nudge(press.Direction, press.Resize, press.Large);
                    var stuck = moved == _box;
                    _box = moved;
                    ShowBox();
                    Say(stuck ? _box.SpokenAtLimit : _box.Spoken);
                }

                return true;
            case BlurBoxKey.Apply when KeyboardBox.IsKeyboardFocused:
                if (_blurring || Model?.Enlarged is { BlurPending: true })
                {
                    Say(StillWaiting);
                }
                else
                {
                    BlurUnderTheBox();
                }

                return true;
            default:
                return false;
        }
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

        // An armed surface must not be left waiting over the next frame, and what the box last said
        // belongs to the frame that was open.
        BlurMode.IsChecked = false;
        BlurBoxStatus.Visibility = System.Windows.Visibility.Collapsed;
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

    /// <summary>
    /// Esc, T, B and Ctrl+Z from any control of the enlarged view, not only from the strip under it
    /// (ST-084); and the blur box's keys while it is out, ahead of them.
    /// </summary>
    private void OnEnlargedKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // A held key repeats, and a repeated Enter or B goes on pressing whatever has focus once the first
        // press has done its work. After a blur that is the toggle, which brings the box back, and then the
        // box, which blurs a second region nobody placed (ST-084 follow-up review). In the enlarged view
        // only the first press of either counts; held arrows still move the box.
        if (e.IsRepeat && key is Key.Enter or Key.B)
        {
            e.Handled = true;
            return;
        }

        if (OnBoxKey(key, e.KeyboardDevice.Modifiers))
        {
            e.Handled = true;
            return;
        }

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

    /// <summary>A selection made from outside (a transcript line, a frame chip) is scrolled into view.</summary>
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

/// <summary>What one keypress does to the blur box.</summary>
public enum BlurBoxKey
{
    None,
    Move,
    Apply,
    Cancel,

    /// <summary>Ctrl+Enter, Publish, held back while a box is placed and not applied.</summary>
    HoldPublish,
}

/// <param name="Kind">What the key does.</param>
/// <param name="Direction">For <see cref="BlurBoxKey.Move"/>, which arrow.</param>
/// <param name="Resize">Shift: the arrow resizes the box rather than moving it.</param>
/// <param name="Large">Ctrl: by <see cref="BlurBox.LargeStep"/> rather than <see cref="BlurBox.Step"/>.</param>
public readonly record struct BlurBoxPress(BlurBoxKey Kind, BlurBoxDirection Direction = BlurBoxDirection.Left, bool Resize = false, bool Large = false);

/// <summary>
/// The blur box's keys (ST-084 follow-up), as one pure routing function so they are tested without a
/// window: the arrows move it, Shift with them resizes it, Ctrl with either takes bigger steps, Enter
/// blurs what it covers and Esc puts it away. Alt is left alone. Ctrl+Enter, which is Publish, is held
/// back while the box is out, so a picture about to be covered is not sent first.
/// </summary>
public static class BlurBoxKeys
{
    /// <param name="key">The key, already read through <see cref="KeyEventArgs.SystemKey"/> when WPF reports <see cref="Key.System"/>.</param>
    public static BlurBoxPress Route(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape)
        {
            return new BlurBoxPress(BlurBoxKey.Cancel);
        }

        if (key == Key.Enter)
        {
            return modifiers switch
            {
                ModifierKeys.None => new BlurBoxPress(BlurBoxKey.Apply),
                ModifierKeys.Control => new BlurBoxPress(BlurBoxKey.HoldPublish),
                _ => default,
            };
        }

        if ((modifiers & ~(ModifierKeys.Shift | ModifierKeys.Control)) != ModifierKeys.None)
        {
            return default;
        }

        BlurBoxDirection? direction = key switch
        {
            Key.Left => BlurBoxDirection.Left,
            Key.Right => BlurBoxDirection.Right,
            Key.Up => BlurBoxDirection.Up,
            Key.Down => BlurBoxDirection.Down,
            _ => null,
        };
        return direction is { } arrow
            ? new BlurBoxPress(BlurBoxKey.Move, arrow, modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Control))
            : default;
    }
}
