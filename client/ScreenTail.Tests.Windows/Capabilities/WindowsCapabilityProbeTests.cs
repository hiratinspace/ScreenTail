using ScreenTail.Core.Capabilities;
using ScreenTail.Service.Capabilities;

namespace ScreenTail.Tests.Windows.Capabilities;

/// <summary>
/// ST-021 against real Windows. These assert the probe's <em>shape</em>, not its answers: a hosted runner
/// has no desktop and no microphone, the laptop has both, and both are correct results. What must hold
/// everywhere is that every capability gets a verdict, nothing throws, and a problem always comes with
/// something to do about it. The laptop's actual answers are published by the hardware-checks workflow.
/// </summary>
public sealed class WindowsCapabilityProbeTests
{
    private static readonly string[] WireStates = ["ok", "degraded", "blocked", "unknown"];

    [Fact]
    public void TheProbeAnswersForEveryCapabilityWithoutThrowing()
    {
        var report = new WindowsCapabilityProbe().Probe();

        Assert.Equal(Enum.GetValues<Capability>().Length, report.Checks.Count);
        Assert.Equal(Enum.GetValues<Capability>().ToHashSet(), report.Checks.Select(c => c.Capability).ToHashSet());
        Assert.InRange(report.CheckedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void EveryProblemComesWithANextStep()
    {
        var report = new WindowsCapabilityProbe().Probe();

        foreach (var check in report.Checks.Where(c => c.State != CapabilityState.Ok))
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Message), $"{check.Capability} has no message");
            Assert.False(string.IsNullOrWhiteSpace(check.FixHint), $"{check.Capability} has no fix hint");
        }
    }

    [Fact]
    public void ProbingTwiceAgrees()
    {
        // Nothing here is one-shot: the hook must be released, the DCs freed. A second run proves it.
        var first = new WindowsCapabilityProbe().Probe();
        var second = new WindowsCapabilityProbe().Probe();

        Assert.Equal(
            first.Checks.Select(c => (c.Capability, c.State)),
            second.Checks.Select(c => (c.Capability, c.State)));
    }

    [Fact]
    public void TheReportSurvivesTheWire()
    {
        var wire = new WindowsCapabilityProbe().Probe().ToWire();

        Assert.All(wire.Checks, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Capability));
            Assert.Contains(c.State, WireStates);
        });
    }

    [Fact]
    public void ScreenCaptureAndHooksAgreeWithTheSessionWeAreIn()
    {
        // The rule ADR-0003 was written about: no desktop, no capture. If a runner ever reports a desktop
        // session while hooks are blocked, that's a genuine finding and this test should say so.
        var report = new WindowsCapabilityProbe().Probe();

        if (report[Capability.DesktopSession].State == CapabilityState.Blocked)
        {
            Assert.False(report.CanCapture);
        }
    }

    [Fact]
    public void AClaimedDesktopSessionCanActuallyShowAWindow()
    {
        // The other direction, and the one nothing checked. The probe said "Running in your desktop
        // session" and reported can_capture: true on a runner where no window could take the foreground
        // and every capture would have come back black - so the HUD would have told a technician their
        // session was being recorded while it was not.
        //
        // The old check asked Environment.UserInteractive, which on .NET for Windows returns true
        // unconditionally, service or not. This asserts the claim against the thing the claim is about.
        var report = new WindowsCapabilityProbe().Probe();
        if (report[Capability.DesktopSession].State != CapabilityState.Ok)
        {
            // Correctly reporting no desktop is a pass. What must not happen is claiming one it hasn't got.
            return;
        }

        using var window = DesktopWindow.Create("capability cross-check");

        Assert.True(
            window.TakeForeground(),
            "The probe reports a desktop session, but a window cannot take the foreground. One of them is "
            + "lying, and the probe is the one a technician trusts.");
    }
}
