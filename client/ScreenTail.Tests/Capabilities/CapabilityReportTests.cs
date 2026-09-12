using System.Text.Json;
using ScreenTail.Core.Capabilities;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Capabilities;

/// <summary>ST-021: what the report says, how it ranks problems, and what goes over the wire.</summary>
public sealed class CapabilityReportTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AHealthyMachineCanCapture()
    {
        var report = new AlwaysCapableProbe().Probe();

        Assert.True(report.CanCapture);
        Assert.Null(report.MostImportantProblem);
        Assert.All(report.Checks, c => Assert.Equal(CapabilityState.Ok, c.State));
        Assert.Equal(Enum.GetValues<Capability>().Length, report.Checks.Count);
    }

    [Theory]
    [InlineData(Capability.DesktopSession)]
    [InlineData(Capability.ScreenCapture)]
    [InlineData(Capability.InputHooks)]
    public void AnythingCaptureDependsOnBlocksCapture(Capability capability)
    {
        var report = Report(Blocked(capability));

        Assert.False(report.CanCapture);
        Assert.Equal(capability, report.MostImportantProblem!.Capability);
    }

    [Theory]
    [InlineData(Capability.Microphone)]
    [InlineData(Capability.ElevatedWindows)]
    public void CaptureStillRunsWithoutTheOptionalOnes(Capability capability)
    {
        // A blocked microphone costs narration, not the session. Elevated blindness is normal.
        var report = Report(Blocked(capability));

        Assert.True(report.CanCapture);
        Assert.Equal(capability, report.MostImportantProblem!.Capability);
    }

    [Fact]
    public void ACheckThatCouldNotRunCountsAsBlocking()
    {
        // "We don't know" must not read as "fine": a probe that throws is a reason not to capture.
        var report = Report(CapabilityCopy.Failed(Capability.ScreenCapture, "access denied"));

        Assert.False(report.CanCapture);
        Assert.True(report[Capability.ScreenCapture].Blocks);
    }

    [Fact]
    public void TheHudShowsTheBlockerBeforeTheInconvenience()
    {
        var report = Report(
            CapabilityCopy.ElevatedWindowsInvisible(),
            CapabilityCopy.MicrophoneBlocked());

        // Degraded is real but survivable; blocked comes first.
        Assert.Equal(Capability.Microphone, report.MostImportantProblem!.Capability);
    }

    [Fact]
    public void TheMicrophoneMessageIsTheOneTheSpecAsksFor()
    {
        var check = CapabilityCopy.MicrophoneBlocked();

        Assert.Equal("Microphone is blocked by Windows privacy settings.", check.Message);
        Assert.Equal("ms-settings:privacy-microphone", check.FixLink);
        Assert.NotNull(check.FixHint);
    }

    [Fact]
    public void TheElevatedWindowMessageIsTheOneTheSpecAsksFor()
    {
        var check = CapabilityCopy.ElevatedWindowsInvisible();

        Assert.Equal("Elevated window — screen not captured.", check.Message);
        Assert.Equal(CapabilityState.Degraded, check.State);
    }

    [Fact]
    public void EveryMessageFollowsTheCopyRules()
    {
        // Spec §6: sentence case, no exclamation marks, and something the technician can act on.
        CapabilityCheck[] all =
        [
            CapabilityCopy.MicrophoneBlocked(),
            CapabilityCopy.MicrophoneBlockedByAdmin(),
            CapabilityCopy.NoMicrophone(),
            CapabilityCopy.ElevatedWindowsInvisible(),
            CapabilityCopy.NoDesktopSession(),
            CapabilityCopy.ScreenCaptureBlocked("test"),
            CapabilityCopy.HooksBlocked("test"),
        ];

        foreach (var check in all)
        {
            Assert.DoesNotContain("!", check.Message, StringComparison.Ordinal);
            Assert.EndsWith(".", check.Message, StringComparison.Ordinal);
            Assert.True(char.IsUpper(check.Message[0]), check.Message);
            Assert.False(string.IsNullOrWhiteSpace(check.FixHint), $"no next step for {check.Capability}");
        }
    }

    [Fact]
    public void TheWireFormIsSnakeCaseAndOmitsWhatIsNotThere()
    {
        var report = Report(CapabilityCopy.MicrophoneBlocked());

        var json = JsonSerializer.Serialize(report.ToWire(requestId: 7), IpcFraming.Json);

        Assert.Contains("\"capability\":\"microphone\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"blocked\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fix_link\":\"ms-settings:privacy-microphone\"", json, StringComparison.Ordinal);
        Assert.Contains("\"can_capture\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"request_id\":7", json, StringComparison.Ordinal);
        // An "ok" check has nothing to fix, so those keys are absent rather than null.
        Assert.DoesNotContain("\"fix_hint\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEventRoundTripsThroughTheContract()
    {
        var sent = new AlwaysCapableProbe().Probe().ToWire();

        var json = JsonSerializer.Serialize<IpcEvent>(sent, IpcFraming.Json);
        var back = Assert.IsType<CapabilitiesReported>(JsonSerializer.Deserialize<IpcEvent>(json, IpcFraming.Json));

        Assert.Equal(sent.CanCapture, back.CanCapture);
        Assert.Equal(sent.Checks.Count, back.Checks.Count);
        Assert.Contains("\"type\":\"capabilities\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCapabilityAndStateHasAWireName()
    {
        foreach (var capability in Enum.GetValues<Capability>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CapabilityWire.Name(capability)));
        }

        foreach (var state in Enum.GetValues<CapabilityState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CapabilityWire.Name(state)));
        }
    }

    /// <summary>A report that is healthy apart from the given checks.</summary>
    private static CapabilityReport Report(params CapabilityCheck[] problems)
    {
        var checks = new AlwaysCapableProbe().Probe().Checks
            .Where(c => problems.All(p => p.Capability != c.Capability))
            .Concat(problems)
            .OrderBy(c => (int)c.Capability)
            .ToList();
        return new CapabilityReport(At, checks);
    }

    private static CapabilityCheck Blocked(Capability capability) => capability switch
    {
        Capability.DesktopSession => CapabilityCopy.NoDesktopSession(),
        Capability.ScreenCapture => CapabilityCopy.ScreenCaptureBlocked("test"),
        Capability.InputHooks => CapabilityCopy.HooksBlocked("test"),
        Capability.Microphone => CapabilityCopy.MicrophoneBlocked(),
        _ => new CapabilityCheck(capability, CapabilityState.Blocked, "Blocked.", "Do something."),
    };
}
