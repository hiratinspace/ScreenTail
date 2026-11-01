namespace ScreenTail.Core.Settings;

/// <summary>What Windows said about the desktop changing hands, in the words <see cref="DesktopPresence"/> needs.</summary>
public enum DesktopSwitch
{
    /// <summary>Locked: Win+L, the screen saver's lock, a lid closed with lock on.</summary>
    Locked,

    /// <summary>Unlocked by the technician.</summary>
    Unlocked,

    /// <summary>A fast user switch or a Remote Desktop session taking the console away, or a disconnect.</summary>
    Disconnected,

    /// <summary>Signed out.</summary>
    LoggedOff,

    /// <summary>Signed in.</summary>
    LoggedOn,

    /// <summary>A connect. It may land on the lock screen, so it says nothing either way.</summary>
    Connected,
}

/// <summary>
/// Whether the technician is at this desktop (ST-080 AC3, review of #244): not locked, not switched or
/// disconnected away, not asleep. The level meter does not open while they are away.
///
/// Only Windows' own word brings them back: an unlock or a sign-in clears a lock, a resume clears sleep.
/// A window being activated proves nothing, because a window can be activated behind the lock screen; it
/// may only make the answer stricter, when the real lock state read at that moment says locked.
/// </summary>
public sealed class DesktopPresence(bool lockedAtStart)
{
    private bool _locked = lockedAtStart;
    private bool _asleep;

    /// <summary>The technician is not at this desktop, so nothing may listen for them.</summary>
    public bool Away => _locked || _asleep;

    public void Switched(DesktopSwitch change)
    {
        switch (change)
        {
            case DesktopSwitch.Locked or DesktopSwitch.Disconnected or DesktopSwitch.LoggedOff:
                _locked = true;
                break;
            case DesktopSwitch.Unlocked or DesktopSwitch.LoggedOn:
                _locked = false;
                break;
            default:
                break;
        }
    }

    public void Suspended() => _asleep = true;

    public void Resumed() => _asleep = false;

    /// <summary>The window came to the front. <paramref name="lockedNow"/> is the lock state read from Windows at that moment.</summary>
    public void WindowActivated(bool lockedNow)
    {
        if (lockedNow)
        {
            _locked = true;
        }
    }
}
