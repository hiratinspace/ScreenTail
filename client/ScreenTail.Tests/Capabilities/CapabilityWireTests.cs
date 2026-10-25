using ScreenTail.Core.Capabilities;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Capabilities;

/// <summary>
/// A capability report crosses the pipe as snake_case names (ST-021) and comes back for the pill's
/// no-mic segment (ST-072, 2026-10-12 review T-372). What the UI reads must be what the service said.
/// </summary>
public sealed class CapabilityWireTests
{
    [Fact]
    public void AReportSurvivesTheRoundTripToTheWireAndBack()
    {
        var report = new CapabilityReport(
            new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
            [
                CapabilityCopy.Ok(Capability.DesktopSession, "ok"),
                CapabilityCopy.MicrophoneBlocked(),
                CapabilityCopy.Ok(Capability.ScreenCapture, "ok"),
                CapabilityCopy.HooksBlocked("denied"),
                CapabilityCopy.ElevatedWindowsInvisible(),
                CapabilityCopy.Failed(Capability.InputHooks, "timed out"),
            ]);

        var back = report.ToWire(requestId: 4).FromWire();

        // The record compares its list by reference, so the pieces are compared.
        Assert.Equal(report.CheckedAt, back.CheckedAt);
        Assert.Equal(report.Checks, back.Checks);
    }

    [Fact]
    public void ACapabilityThisBuildDoesNotKnowIsLeftOutAndAStateItDoesNotKnowReadsAsUnknown()
    {
        var wire = new CapabilitiesReported
        {
            CheckedAt = DateTimeOffset.UnixEpoch,
            CanCapture = true,
            Checks =
            [
                new CapabilityStatus { Capability = "microphone", State = "degraded", Message = "No microphone found." },
                new CapabilityStatus { Capability = "holographic_display", State = "ok", Message = "ok" },
                new CapabilityStatus { Capability = "screen_capture", State = "partly", Message = "?" },
            ],
        };

        var report = wire.FromWire();

        Assert.Equal([Capability.Microphone, Capability.ScreenCapture], report.Checks.Select(c => c.Capability));
        Assert.Equal(CapabilityState.Unknown, report[Capability.ScreenCapture].State);
        Assert.Equal(CapabilityState.Degraded, report[Capability.Microphone].State);
    }

    [Fact]
    public void ThePillShowsNoMicWhenTheServiceSaysSoAndNothingWhenItSaysNothingAboutIt()
    {
        var degraded = new CapabilitiesReported
        {
            CheckedAt = DateTimeOffset.UnixEpoch,
            CanCapture = true,
            Checks = [new CapabilityStatus { Capability = "microphone", State = "degraded", Message = "No microphone found." }],
        }.FromWire();
        var silent = new CapabilitiesReported { CheckedAt = DateTimeOffset.UnixEpoch, CanCapture = true, Checks = [] }.FromWire();
        var capture = new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1" };

        Assert.NotNull(Core.Hud.HudState.For(capture, degraded).Mic);
        Assert.Null(Core.Hud.HudState.For(capture, silent).Mic);
    }
}
