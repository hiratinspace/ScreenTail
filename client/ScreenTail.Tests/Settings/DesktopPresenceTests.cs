using ScreenTail.Core.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Whether the technician is at the desktop, for Settings' level meter (ST-080 AC3, re-review of #244).
/// Only Windows' own word brings them back; a window being activated never does.
/// </summary>
public sealed class DesktopPresenceTests
{
    [Fact]
    public void AnActivationWhileLockedKeepsTheTechnicianAway()
    {
        // A window can come to the front behind the lock screen. The meter must not open there.
        var presence = new DesktopPresence(lockedAtStart: false);
        presence.Switched(DesktopSwitch.Locked);

        // Even when the lock read at that moment comes back unlocked: only the unlock event clears it.
        presence.WindowActivated(lockedNow: false);
        Assert.True(presence.Away);

        presence.WindowActivated(lockedNow: true);
        Assert.True(presence.Away);
        presence.Switched(DesktopSwitch.Unlocked);
        Assert.False(presence.Away);
    }

    [Fact]
    public void AnActivationWhileAsleepDoesNotWakeIt()
    {
        var presence = new DesktopPresence(lockedAtStart: false);
        presence.Suspended();

        presence.WindowActivated(lockedNow: false);

        Assert.True(presence.Away);
        presence.Resumed();
        Assert.False(presence.Away);
    }

    [Fact]
    public void ADesktopLockedWhenTheCardLoadsIsAwayUntilUnlocked()
    {
        var presence = new DesktopPresence(lockedAtStart: true);

        presence.WindowActivated(lockedNow: false);
        Assert.True(presence.Away);

        presence.Switched(DesktopSwitch.Connected);
        Assert.True(presence.Away);
        presence.Switched(DesktopSwitch.LoggedOn);
        Assert.False(presence.Away);
    }

    [Fact]
    public void AnActivationThatFindsTheDesktopLockedMakesItAway()
    {
        // A lock whose event never arrived is still caught when the window is next activated.
        var presence = new DesktopPresence(lockedAtStart: false);

        presence.WindowActivated(lockedNow: true);

        Assert.True(presence.Away);
    }

    [Theory]
    [InlineData(DesktopSwitch.Locked, true)]
    [InlineData(DesktopSwitch.Disconnected, true)]
    [InlineData(DesktopSwitch.LoggedOff, true)]
    [InlineData(DesktopSwitch.Connected, false)]
    public void EachSwitchAwayIsAway(DesktopSwitch change, bool away)
    {
        var presence = new DesktopPresence(lockedAtStart: false);

        presence.Switched(change);

        Assert.Equal(away, presence.Away);
    }

    [Fact]
    public void AConnectDoesNotBringBackALockedDesktop()
    {
        var presence = new DesktopPresence(lockedAtStart: false);
        presence.Switched(DesktopSwitch.Disconnected);

        presence.Switched(DesktopSwitch.Connected);

        Assert.True(presence.Away);
    }
}
