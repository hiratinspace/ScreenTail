using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenTail.UI.Settings.Capture;

/// <summary>
/// Layout only. Every rule is <see cref="Core.Settings.CapturePanel"/>'s.
///
/// The one thing worked out here is a view's own fact: whether the microphone card is being looked at
/// (ST-080 AC3). It is on screen, scrolled at least partly into view, and its window is in front and not
/// minimised. The live level is read for exactly as long as that is true, so the microphone does not
/// open for a Settings window left behind another one, scrolled past or minimised to the taskbar, and it
/// closes the moment the technician switches to the remote session.
/// </summary>
public partial class CapturePane : UserControl
{
    private Window? _window;
    private ScrollViewer? _scroller;
    private bool _watched;

    public CapturePane()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => Recheck();
        DataContextChanged += (_, e) =>
        {
            // A new view model starts unwatched; the old one is told it is no longer looked at.
            if (e.OldValue is CaptureViewModel old && _watched)
            {
                _ = old.SetMicrophoneCardWatchedAsync(false);
            }

            _watched = false;
            Recheck();
        };
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += Changed;
            _window.Deactivated += Changed;
            _window.StateChanged += Changed;
            _window.IsVisibleChanged += VisibilityChanged;
        }

        _scroller = Ancestor<ScrollViewer>(this);
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += Changed;
        }

        Recheck();
    }

    private void Detach()
    {
        if (_window is not null)
        {
            _window.Activated -= Changed;
            _window.Deactivated -= Changed;
            _window.StateChanged -= Changed;
            _window.IsVisibleChanged -= VisibilityChanged;
            _window = null;
        }

        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= Changed;
            _scroller = null;
        }

        Set(false);
    }

    private void Changed(object? sender, EventArgs e) => Recheck();

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Recheck();

    private void Recheck() => Set(LookedAt());

    private bool LookedAt()
    {
        if (!IsLoaded || !IsVisible || !MicrophoneCard.IsVisible || _window is not { IsActive: true, IsVisible: true } || _window.WindowState == WindowState.Minimized)
        {
            return false;
        }

        if (_scroller is null)
        {
            return true;
        }

        try
        {
            var bounds = MicrophoneCard.TransformToAncestor(_scroller).TransformBounds(new Rect(MicrophoneCard.RenderSize));
            return bounds.Bottom > 0 && bounds.Top < _scroller.ViewportHeight;
        }
        catch (InvalidOperationException)
        {
            // Not in the scroller's tree after all: nothing to say it is in view, so it is not.
            return false;
        }
    }

    private void Set(bool watched)
    {
        if (watched == _watched)
        {
            return;
        }

        _watched = watched;
        if (DataContext is CaptureViewModel model)
        {
            _ = model.SetMicrophoneCardWatchedAsync(watched);
        }
    }

    private static T? Ancestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T found)
            {
                return found;
            }
        }

        return null;
    }
}
