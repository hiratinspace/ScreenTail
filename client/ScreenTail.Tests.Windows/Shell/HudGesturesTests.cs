using System.Runtime.Versioning;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Hud;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The pill's gestures and segments reach the shell (Spec §5 S2; 2026-10-12 review T-372). Only Mark
/// moment used to: double-click, right-click Hide, no-mic and offline worked in the render harness alone.
///
/// No window and no WPF Application: WPF allows one Application per process and another test owns it, so
/// the pill's view model and its gestures are held here without the window that draws them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HudGesturesTests
{
    [Fact]
    public void ThePillsGesturesReachTheShell()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.DraftReady, SessionId = "s1" });
        var model = new HudViewModel(state);
        var shellShows = true;
        var gestures = new HudGestures(model, () => shellShows);
        var reviews = 0;
        var hides = 0;
        gestures.ReviewRequested += () => reviews++;
        gestures.HideRequested += () =>
        {
            hides++;
            shellShows = false;
        };

        model.ReviewCommand.Execute(null);
        model.HideCommand.Execute(null);

        Assert.Equal(1, reviews);
        Assert.Equal(1, hides);

        // Put away, later state changes repeat the same wish and say nothing new.
        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Idle });
        Assert.Equal(1, hides);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    public void RightClickingThePillDuringASessionAsksTheShellForNothing(string running)
    {
        // INV-4: the pill stays up while there is something to indicate, so there is nothing to ask for.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = running, SessionId = "s1" });
        var model = new HudViewModel(state);
        var gestures = new HudGestures(model, () => true);
        var hides = 0;
        gestures.HideRequested += () => hides++;

        model.HideCommand.Execute(null);
        state.Observe(new CaptureStateSnapshot { State = running, SessionId = "s1", ElapsedMs = 5_000 });

        Assert.Equal(0, hides);
        Assert.True(model.Visible);
    }

    [Fact]
    public void ThePillsNoMicAndOfflineSegmentsFollowWhatTheShellIsTold()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1" });
        var model = new HudViewModel(state);
        Assert.False(model.ShowMic);
        Assert.False(model.ShowCloud);

        model.Capabilities = new CapabilitiesReported
        {
            CheckedAt = DateTimeOffset.UnixEpoch,
            CanCapture = true,
            Checks = [new CapabilityStatus { Capability = "microphone", State = "blocked", Message = "Microphone access is off." }],
        }.FromWire();
        model.Online = false;

        Assert.True(model.ShowMic);
        Assert.True(model.ShowCloud);
    }
}
