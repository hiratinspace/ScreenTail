using ScreenTail.Core.Hud;

namespace ScreenTail.Tests.Hud;

/// <summary>
/// Where the recording pill is allowed to be (ST-072, INV-4; 2026-09-20 review).
///
/// The pill is the thing a customer can point at to know their screen is being recorded, and the service
/// treats "a UI is attached" as proof that it is showing. The window restored whatever position was last
/// saved, with no check that the position still exists: a technician who docked the pill on a second
/// monitor and then unplugged it got a pill drawn into empty coordinate space, off every screen, while
/// the service went on recording and counting itself indicated.
///
/// Nothing malicious is needed for that. Unplugging a monitor is Tuesday.
/// </summary>
public sealed class HudPlacementTests
{
    private static readonly ScreenArea Laptop = new(0, 0, 1920, 1080);
    private static readonly ScreenArea Second = new(1920, 0, 3840, 1080);

    [Fact]
    public void APillWhereTheScreenIsStaysWhereItWasPut()
    {
        Assert.True(HudPlacement.IsOnScreen(1600, 40, 220, 44, [Laptop]));
    }

    [Fact]
    public void APillOnAMonitorThatIsNoLongerThereIsNot()
    {
        // Saved while the second monitor was plugged in, restored after it was unplugged.
        Assert.False(HudPlacement.IsOnScreen(3600, 40, 220, 44, [Laptop]));
    }

    [Fact]
    public void APillOnAMonitorThatIsStillThereIsFine()
    {
        Assert.True(HudPlacement.IsOnScreen(3600, 40, 220, 44, [Laptop, Second]));
    }

    [Fact]
    public void APillAcrossTheJoinBetweenTwoScreensCounts()
    {
        // Half on each, which is a thing people do on purpose. Re-docking it on every start would be a
        // bug of its own, so the middle of the pill is what has to land somewhere real.
        Assert.True(HudPlacement.IsOnScreen(1820, 40, 220, 44, [Laptop, Second]));
    }

    [Fact]
    public void APositionNobodyCouldHaveDraggedItToIsRefused()
    {
        // shell.json is a file in the technician's own profile, so a same-user process can write this.
        Assert.False(HudPlacement.IsOnScreen(99_999, 99_999, 220, 44, [Laptop, Second]));
    }

    [Fact]
    public void APillHangingOffTheBottomEdgeIsRefused()
    {
        // Its top-left is on the screen and its middle is not, which is the case a corner check misses.
        Assert.False(HudPlacement.IsOnScreen(900, 1070, 220, 44, [Laptop]));
    }

    [Fact]
    public void WithNoScreensAtAllNothingIsOnOne()
    {
        // What the virtual screen looks like on a locked or headless machine. Docking is the safe answer.
        Assert.False(HudPlacement.IsOnScreen(10, 10, 220, 44, []));
    }
}
