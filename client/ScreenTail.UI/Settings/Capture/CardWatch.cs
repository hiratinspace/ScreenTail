using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ScreenTail.Core.Settings;

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
/// so they are taken while the card is loaded and given back when it unloads. Whether the desktop is
/// locked when the card loads, and again whenever its window is activated, is read from Windows itself;
/// only an unlock, a sign-in or a resume brings the card back (<see cref="DesktopPresence"/>).
/// </summary>
internal sealed class CardWatch
{
    private readonly FrameworkElement _card;
    private readonly Action<bool> _watched;
    private Window? _window;
    private ScrollViewer? _scroller;
    private DesktopPresence _presence = new(lockedAtStart: false);

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

    /// <summary>Whether Windows' input desktop is locked right now. Read from Windows; a test passes its own.</summary>
    public Func<bool> DesktopLocked { get; set; } = InputDesktopLocked;

    /// <summary>Looks again, for a change no event announced (a test's <see cref="InFront"/>).</summary>
    public void Recheck() => Tell(LookedAt());

    /// <summary>Windows said the desktop changed hands: locked or unlocked, switched, connected or disconnected.</summary>
    public void SessionSwitched(SessionSwitchReason reason)
    {
        _presence.Switched(reason switch
        {
            SessionSwitchReason.SessionLock => DesktopSwitch.Locked,
            SessionSwitchReason.SessionUnlock => DesktopSwitch.Unlocked,
            SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => DesktopSwitch.Disconnected,
            SessionSwitchReason.SessionLogoff => DesktopSwitch.LoggedOff,
            SessionSwitchReason.SessionLogon => DesktopSwitch.LoggedOn,
            _ => DesktopSwitch.Connected,
        });
        Recheck();
    }

    /// <summary>Windows said the machine is going to sleep or has woken.</summary>
    public void PowerChanged(PowerModes mode)
    {
        switch (mode)
        {
            case PowerModes.Suspend:
                _presence.Suspended();
                break;
            case PowerModes.Resume:
                _presence.Resumed();
                break;
            default:
                return;
        }

        Recheck();
    }

    /// <summary>
    /// The window came to the front. That proves nothing about the desktop (a window can be activated
    /// behind the lock screen), so it only reads the lock state from Windows, which can keep the card off
    /// but never brings it back.
    /// </summary>
    public void WindowActivated()
    {
        _presence.WindowActivated(DesktopLocked());
        Recheck();
    }

    private void Attach()
    {
        // The events may have come while the card was not loaded: start from what Windows says now.
        _presence = new DesktopPresence(DesktopLocked());
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

    private void Activated(object? sender, EventArgs e) => WindowActivated();

    private void Changed(object? sender, EventArgs e) => Recheck();

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Recheck();

    private bool LookedAt()
    {
        if (_presence.Away || !_card.IsLoaded || !_card.IsVisible
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

    /// <summary>
    /// Locked, or any desktop that is not the technician's: the input desktop cannot be opened, or cannot
    /// be switched to, when the secure desktop (the lock screen, a sign-in, a UAC prompt) has the input.
    /// A failure to ask reads as locked, so the meter stays off rather than guessing.
    /// </summary>
    private static bool InputDesktopLocked()
    {
        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            return !SwitchDesktop(desktop);
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwitchDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

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
