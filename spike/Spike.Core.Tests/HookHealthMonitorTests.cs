namespace ScreenTail.Spike.Core.Tests;

public class HookHealthMonitorTests
{
    [Fact]
    public void HookKeepingUp_IsNeverFlagged()
    {
        var monitor = new HookHealthMonitor(toleranceMs: 2000, strikesToFlag: 3);

        for (uint t = 0; t < 100; t++)
        {
            monitor.Check(lastSystemInputTick: 10_000 + t, lastHookTick: 10_000 + t);
        }

        Assert.False(monitor.SuspectedUnhooked);
        Assert.Equal(0, monitor.TotalStrikes);
    }

    [Fact]
    public void ThreeConsecutiveLags_FlagsUnhook()
    {
        var monitor = new HookHealthMonitor(toleranceMs: 2000, strikesToFlag: 3);

        monitor.Check(20_000, 10_000);
        monitor.Check(21_000, 10_000);
        Assert.False(monitor.SuspectedUnhooked);

        monitor.Check(22_000, 10_000);
        Assert.True(monitor.SuspectedUnhooked);
    }

    [Fact]
    public void RecoveryBetweenStrikes_ResetsTheCount()
    {
        var monitor = new HookHealthMonitor(toleranceMs: 2000, strikesToFlag: 3);

        monitor.Check(20_000, 10_000);
        monitor.Check(21_000, 10_000);
        monitor.Check(21_000, 21_000);
        monitor.Check(30_000, 21_000);

        Assert.False(monitor.SuspectedUnhooked);
        Assert.Equal(3, monitor.TotalStrikes);
    }

    [Fact]
    public void TickWraparound_IsHandled()
    {
        var monitor = new HookHealthMonitor(toleranceMs: 2000, strikesToFlag: 1);
        const uint hookTick = uint.MaxValue - 100;

        monitor.Check(lastSystemInputTick: 50, lastHookTick: hookTick);
        Assert.False(monitor.SuspectedUnhooked);

        monitor.Check(lastSystemInputTick: 5000, lastHookTick: hookTick);
        Assert.True(monitor.SuspectedUnhooked);
    }

    [Fact]
    public void HookNewerThanSystem_IsNotAStrike()
    {
        var monitor = new HookHealthMonitor(toleranceMs: 2000, strikesToFlag: 1);

        monitor.Check(lastSystemInputTick: 1000, lastHookTick: 9000);

        Assert.False(monitor.SuspectedUnhooked);
    }
}
