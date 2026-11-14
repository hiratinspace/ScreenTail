namespace ScreenTail.Core.Detection;

/// <summary>
/// Which WinEvents are allowed to say that the foreground window has changed (ST-022, ST-048).
///
/// Two events carry that news. <c>EVENT_SYSTEM_FOREGROUND</c> is the foreground actually moving, and
/// <c>EVENT_OBJECT_NAMECHANGE</c> is the window in front retitling itself, which matters because the
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

    /// <summary>The object id a window's own title change is raised with. <c>OBJID_WINDOW</c>.</summary>
    public const int WindowObject = 0;

    /// <summary>
    /// The window an event is news about, or zero when it is news about nothing in front. Only the window in
    /// front counts, and Windows' answer to "what is in front now" is the one source of it (ST-031).
    ///
    /// A rename is news only from the window in front, as its own title (<see cref="WindowObject"/>): every
    /// other window renaming itself is noise, and there is a lot of it. A foreground event is news about the
    /// window in front now, which is usually the window it names, but not always: an event handed over after
    /// the foreground has moved on, or one an application raised about a window of its own, names a window
    /// that is not in front. The watcher moves its name hook to the thread of the window this returns and
    /// checks renames against the window in front, so taking the event's word would leave the hook on a
    /// thread whose window is not in front, and every rename of the window that is would be dropped until the
    /// next foreground change or the poll. While Windows names nothing in front, for a moment during a
    /// switch, the event's own window is the best answer there is.
    /// </summary>
    /// <param name="eventType">The event's type, one <see cref="Interesting"/> accepts.</param>
    /// <param name="window">The window the event names.</param>
    /// <param name="objectId">The event's object id.</param>
    /// <param name="foreground">The window Windows says is in front now (<c>GetForegroundWindow</c>), or zero.</param>
    public static nint WindowInFront(uint eventType, nint window, int objectId, nint foreground)
    {
        if (window == 0 || !Interesting(eventType))
        {
            return 0;
        }

        if (eventType == ObjectNameChange)
        {
            return objectId == WindowObject && window == foreground ? window : 0;
        }

        return foreground != 0 ? foreground : window;
    }

    /// <summary>The window class of a classic console, drawn by conhost.</summary>
    public const string ClassicConsoleClass = "ConsoleWindowClass";

    /// <summary>
    /// Whether a window of this class retitles itself on a thread other than the one Windows names as its
    /// owner, so a name-change hook narrowed to the owner's thread would never hear it (ST-031, review of
    /// #252). A classic console is the case: conhost draws the window and raises the rename, and Windows
    /// names the shell running inside it as the owner, for compatibility. Windows Terminal draws its own
    /// window and is not one.
    /// </summary>
    public static bool OwnerIsNotTheRenamer(string? className) =>
        string.Equals(className, ClassicConsoleClass, StringComparison.Ordinal);
}
