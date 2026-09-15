namespace ScreenTail.Core.Detection;

/// <summary>
/// Which WinEvents are allowed to say that the foreground window has changed (ST-022, ST-048).
///
/// Two events carry that news. <c>EVENT_SYSTEM_FOREGROUND</c> is the foreground actually moving, and
/// <c>EVENT_OBJECT_NAMECHANGE</c> is the window in front retitling itself — which matters because the
/// title is where a remote-session window says which machine it is connected to, and a scope decision
/// (INV-5) turns on that answer.
///
/// Everything else is noise, and there is an enormous amount of it: a window being dragged, a control
/// taking focus, a menu opening, a console caret moving. Those events arrive with their own window
/// handle, which is usually not the foreground window at all, so acting on one publishes the wrong
/// window as the foreground and re-runs the scope decision against it.
///
/// This is a Core rule rather than a line in the Windows adapter because the adapter got it wrong:
/// <c>SetWinEventHook</c> takes an inclusive <c>eventMin</c>/<c>eventMax</c> range, the two constants
/// below were passed as though they were a pair, and the watcher quietly subscribed to the 32,778 event
/// types between them. A rule that can be read and tested without a Windows machine is a rule that gets
/// checked.
/// </summary>
public static class ForegroundEvents
{
    /// <summary>The foreground window changed. <c>EVENT_SYSTEM_FOREGROUND</c>.</summary>
    public const uint SystemForeground = 0x0003;

    /// <summary>A window changed its title. <c>EVENT_OBJECT_NAMECHANGE</c>.</summary>
    public const uint ObjectNameChange = 0x800C;

    /// <summary>Whether this event type may change what the watcher believes is in front.</summary>
    public static bool Interesting(uint eventType) => eventType is SystemForeground or ObjectNameChange;
}
