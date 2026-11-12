using ScreenTail.Core.Detection;

namespace ScreenTail.Tests.Detection;

/// <summary>
/// ST-048 (weaknesses P0-5). The watcher subscribed with <c>SetWinEventHook(0x0003, 0x800C, …)</c>,
/// reading the two arguments as "these two events" when Windows reads them as an inclusive range — every
/// event from <c>EVENT_SYSTEM_FOREGROUND</c> to <c>EVENT_OBJECT_NAMECHANGE</c>, which is nearly the whole
/// table. Each one arrived carrying its own window handle, and every handle that differed from the last
/// was published as the foreground window, so scope decisions (INV-5) and the typing gate (INV-6) were
/// being fed windows that were never in front.
///
/// The range is now two hooks, and this is the second line of defence: whatever a hook delivers, only
/// these two event types are allowed to say what the foreground window is. It lives in Core so the rule
/// can be argued about without a Windows machine, which is where it went wrong the first time.
/// </summary>
public sealed class ForegroundEventTests
{
    [Fact]
    public void TheTwoEventsTheWatcherAsksForAreAccepted()
    {
        Assert.True(ForegroundEvents.Interesting(ForegroundEvents.SystemForeground));
        Assert.True(ForegroundEvents.Interesting(ForegroundEvents.ObjectNameChange));
    }

    [Theory]
    // Everything below was inside the old inclusive range. The first three are the expensive ones: a
    // window being dragged, a menu opening and a control taking focus each fire continuously while a
    // technician works, and a remote-desktop control redrawing under the cursor fires the first of them
    // at the frame rate of the session.
    [InlineData(0x800Bu, "EVENT_OBJECT_LOCATIONCHANGE")]
    [InlineData(0x8005u, "EVENT_OBJECT_FOCUS")]
    [InlineData(0x8002u, "EVENT_OBJECT_SHOW")]
    [InlineData(0x8003u, "EVENT_OBJECT_HIDE")]
    [InlineData(0x8004u, "EVENT_OBJECT_REORDER")]
    [InlineData(0x800Au, "EVENT_OBJECT_STATECHANGE")]
    [InlineData(0x0004u, "EVENT_SYSTEM_MENUSTART")]
    [InlineData(0x0016u, "EVENT_SYSTEM_MINIMIZESTART")]
    [InlineData(0x4001u, "EVENT_CONSOLE_CARET")]
    [InlineData(0x0001u, "EVENT_SYSTEM_SOUND")]
    public void EverythingElseInTheOldRangeIsRefused(uint eventType, string name)
    {
        Assert.False(ForegroundEvents.Interesting(eventType), $"{name} (0x{eventType:X4}) must not move the foreground window");
    }

    [Fact]
    public void TheRangeTheWatcherUsedToSubscribeToWasNearlyTheWholeTable()
    {
        // The measurement that makes the bug concrete rather than theoretical, and a guard against anyone
        // reading the two constants as a range again.
        var swept = 0;
        for (var e = ForegroundEvents.SystemForeground; e <= ForegroundEvents.ObjectNameChange; e++)
        {
            if (!ForegroundEvents.Interesting(e))
            {
                swept++;
            }
        }

        Assert.Equal(32_776, swept);
    }

    private const nint Front = 0x1001;
    private const nint Behind = 0x2002;

    [Fact]
    public void AForegroundEventNamingAWindowNoLongerInFrontIsAboutTheWindowInFront()
    {
        // The name hook follows the window this answers with, and only renames of the window in front pass
        // (the rule below). They used to come from two places: the hook followed the window the foreground
        // event named, and renames were checked against GetForegroundWindow. A foreground event that names a
        // window no longer in front (handed over late, or raised by an application about a window of its
        // own) left the hook on that window's thread while another window was in front, and every rename of
        // the window in front went unheard until the next foreground change or the poll: the laptop's failure
        // of RetitlingTheWindowInFrontIsReported, with the window in front throughout (ST-031).
        Assert.Equal(Front, ForegroundEvents.WindowInFront(ForegroundEvents.SystemForeground, Behind, ForegroundEvents.WindowObject, Front));
    }

    [Fact]
    public void AForegroundEventIsAboutItsOwnWindowWhileWindowsNamesNothingInFront()
    {
        // The desktop is without a foreground for a moment during a switch. The event's window is then the
        // best answer there is, as it always was, rather than reporting nothing until the poll.
        Assert.Equal(Behind, ForegroundEvents.WindowInFront(ForegroundEvents.SystemForeground, Behind, ForegroundEvents.WindowObject, 0));
        Assert.Equal(Front, ForegroundEvents.WindowInFront(ForegroundEvents.SystemForeground, Front, ForegroundEvents.WindowObject, Front));
    }

    [Fact]
    public void OnlyTheWindowInFrontRetitlingItselfIsNews()
    {
        Assert.Equal(Front, ForegroundEvents.WindowInFront(ForegroundEvents.ObjectNameChange, Front, ForegroundEvents.WindowObject, Front));

        // A window behind renaming itself, a control inside the window in front, and a window with nothing
        // in front.
        Assert.Equal(0, ForegroundEvents.WindowInFront(ForegroundEvents.ObjectNameChange, Behind, ForegroundEvents.WindowObject, Front));
        Assert.Equal(0, ForegroundEvents.WindowInFront(ForegroundEvents.ObjectNameChange, Front, -4, Front));
        Assert.Equal(0, ForegroundEvents.WindowInFront(ForegroundEvents.ObjectNameChange, Front, ForegroundEvents.WindowObject, 0));
    }

    [Fact]
    public void AnEventWithNoWindowOrOfAnotherTypeIsAboutNothing()
    {
        Assert.Equal(0, ForegroundEvents.WindowInFront(ForegroundEvents.SystemForeground, 0, ForegroundEvents.WindowObject, Front));
        Assert.Equal(0, ForegroundEvents.WindowInFront(0x800Bu, Front, ForegroundEvents.WindowObject, Front));
    }
}
