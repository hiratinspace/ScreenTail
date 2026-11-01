using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ScreenTail.UI.Settings.Capture;

/// <summary>
/// Whether a card is being looked at (ST-080 AC3): a view's own fact, worked out here so the microphone's
/// live level is read for exactly as long as it is true.
///
/// Looked at means all of: the card is shown, scrolled at least partly into view, its window is shown,
/// in front and not minimised, and the desktop is the technician's (not locked, switched away from,
/// disconnected or asleep). The last needs Windows' own events: locking with Win+L, a fast user switch, a
/// Remote Desktop disconnect or a sleep may leave the window looking active to WPF, and the microphone
/// must not stay open behind a lock screen. Those events are process-wide and hold whatever subscribes,
/// so they are taken while the card is loaded and given back when it unloads.
/// </summary>
internal sealed class CardWatch
{
    private readonly FrameworkElement _card;
    private readonly Action<bool> _watched;
    private Window? _window;
    private ScrollViewer? _scroller;
    private bool _locked;
    private bool _asleep;

    /// <param name="watched">Told each time the answer changes, on the card's thread.</param>
    public CardWatch(FrameworkElement card, Action<bool> watched)
    {
        _card = card ?? throw new ArgumentNullException(nameof(card));
        _watched = watched ?? throw new ArgumentNullException(nameof(watched));
        _card.Loaded += (_, _) => Attach();
        _card.Unloaded += (_, _) => Detach();
        _card.IsVisibleChanged += (_, _) => Recheck();
        if (_card.IsLoaded)
        {
            Attach();
        }
    }

    /// <summary>The answer last given.</summary>
    public bool Watched { get; private set; }

    /// <summary>Whether Windows' session and power events are held now. A test reads it to see they are given back.</summary>
    public bool HoldsSystemEvents { get; private set; }

    /// <summary>How "in front" is read. The window's own <see cref="Window.IsActive"/>; a test that cannot activate a window passes its own.</summary>
    public Func<Window, bool> InFront { get; set; } = static window => window.IsActive;

    /// <summary>Looks again, for a change no event announced (a test's <see cref="InFront"/>).</summary>
    public void Recheck() => Tell(LookedAt());

    /// <summary>Windows said the desktop changed hands: locked or unlocked, switched, connected or disconnected.</summary>
    public void SessionSwitched(SessionSwitchReason reason)
    {
        switch (reason)
        {
            case SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon:
                _locked = false;
                break;
            default:
                // A connect may land on the lock screen; the unlock that follows says the technician is back.
                return;
        }

        Recheck();
    }

    /// <summary>Windows said the machine is going to sleep or has woken.</summary>
    public void PowerChanged(PowerModes mode)
    {
        switch (mode)
        {
            case PowerModes.Suspend:
                _asleep = true;
                break;
            case PowerModes.Resume:
                _asleep = false;
                break;
            default:
                return;
        }

        Recheck();
    }

    private void Attach()
    {
        _window = Window.GetWindow(_card);
        if (_window is not null)
        {
            _window.Activated += Activated;
            _window.Deactivated += Changed;
            _window.StateChanged += Changed;
            _window.IsVisibleChanged += VisibilityChanged;
        }

        _scroller = Ancestor<ScrollViewer>(_card);
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += Changed;
        }

        if (!HoldsSystemEvents)
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            HoldsSystemEvents = true;
        }

        Recheck();
    }

    private void Detach()
    {
        if (HoldsSystemEvents)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            HoldsSystemEvents = false;
        }

        if (_window is not null)
        {
            _window.Activated -= Activated;
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

        Tell(false);
    }

    // Windows raises these on a thread of its own; the answer is worked out on the card's.
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _ = _card.Dispatcher.BeginInvoke(() => SessionSwitched(e.Reason));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => _ = _card.Dispatcher.BeginInvoke(() => PowerChanged(e.Mode));

    private void Activated(object? sender, EventArgs e)
    {
        // The technician brought the window to the front, so they are at this desktop and awake.
        _locked = false;
        _asleep = false;
        Recheck();
    }

    private void Changed(object? sender, EventArgs e) => Recheck();

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Recheck();

    private bool LookedAt()
    {
        if (_locked || _asleep || !_card.IsLoaded || !_card.IsVisible
            || _window is not { IsVisible: true } window || window.WindowState == WindowState.Minimized || !InFront(window))
        {
            return false;
        }

        if (_scroller is null)
        {
            return true;
        }

        try
        {
            var bounds = _card.TransformToAncestor(_scroller).TransformBounds(new Rect(_card.RenderSize));
            return bounds.Bottom > 0 && bounds.Top < _scroller.ViewportHeight;
        }
        catch (InvalidOperationException)
        {
            // Not in the scroller's tree after all: nothing to say it is in view, so it is not.
            return false;
        }
    }

    private void Tell(bool watched)
    {
        if (watched == Watched)
        {
            return;
        }

        Watched = watched;
        _watched(watched);
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
