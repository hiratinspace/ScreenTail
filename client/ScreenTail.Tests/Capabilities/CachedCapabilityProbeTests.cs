using ScreenTail.Core.Capabilities;

namespace ScreenTail.Tests.Capabilities;

/// <summary>
/// Weakness P2-7 (2026-09-15 review): the Windows probe installs and removes a global low-level mouse
/// hook every time it runs, and the controller ran it on every request. That was latent while nothing
/// in the live UI asked; the onboarding wizard's Permissions step asks on arrival and on "Check again"
/// (ST-083), so it is live now. A report is good for a few seconds: a technician who revokes the
/// microphone and presses "Check again" inside that window sees the old answer, which the button's
/// second press corrects, and nothing else on the machine feels a hook per click.
/// </summary>
public sealed class CachedCapabilityProbeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithinTheWindowTheSameReportIsHandedBackWithoutProbingAgain()
    {
        var inner = new CountingProbe();
        var time = new ManualTime(T0);
        var probe = new CachedCapabilityProbe(inner, time, TimeSpan.FromSeconds(5));

        var first = probe.Probe();
        time.Advance(TimeSpan.FromSeconds(4));
        var second = probe.Probe();

        Assert.Same(first, second);
        Assert.Equal(1, inner.Probes);
    }

    [Fact]
    public void PastTheWindowItProbesAgain()
    {
        var inner = new CountingProbe();
        var time = new ManualTime(T0);
        var probe = new CachedCapabilityProbe(inner, time, TimeSpan.FromSeconds(5));

        _ = probe.Probe();
        time.Advance(TimeSpan.FromSeconds(6));
        var later = probe.Probe();

        Assert.Equal(2, inner.Probes);
        Assert.Equal(T0.AddSeconds(6), later.CheckedAt);
    }

    [Fact]
    public void TheDefaultWindowIsTheReviewsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), CachedCapabilityProbe.DefaultWindow);
    }

    private sealed class CountingProbe : ICapabilityProbe
    {
        public int Probes { get; private set; }

        public CapabilityReport Probe()
        {
            Probes++;
            return new CapabilityReport(T0.AddSeconds(Probes == 1 ? 0 : 6), [CapabilityCopy.Ok(Capability.Microphone, "ok")]);
        }
    }
}
