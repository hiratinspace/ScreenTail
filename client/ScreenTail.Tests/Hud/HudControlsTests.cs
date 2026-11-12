using ScreenTail.Core.Hud;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Hud;

/// <summary>
/// Spec §5 S2 (v0.6.0): Pause, Stop and Mark on the pill, each one click, each offered only where the
/// service would accept it. The pill exposes what the session machine already does and decides nothing:
/// a button the machine would refuse is a button that silently does nothing, which on the one indicator
/// INV-4 is about reads as broken.
/// </summary>
public sealed class HudControlsTests
{
    [Fact]
    public void RecordingOffersMarkPauseAndStopInThatOrder()
    {
        var controls = HudControls.For(Capture(CaptureStates.Recording));

        Assert.Equal([HudAction.Mark, HudAction.Pause, HudAction.Stop], controls.Select(c => c.Action));
    }

    [Fact]
    public void PausedByTheTechnicianOffersResumeAndStopButNotMark()
    {
        var controls = HudControls.For(Capture(CaptureStates.Paused, CaptureReasons.User));

        // The machine refuses a mark unless it is recording (SessionMachine.MarkMomentAsync).
        Assert.Equal([HudAction.Resume, HudAction.Stop], controls.Select(c => c.Action));
    }

    [Theory]
    [InlineData(CaptureReasons.PasswordField)]
    [InlineData(CaptureReasons.ExcludedApp)]
    [InlineData(CaptureReasons.ElevatedWindow)]
    [InlineData(CaptureReasons.OutOfScope)]
    public void SuppressedOffersPauseAndStop(string reason)
    {
        // The machine pauses from suppressed as well as from recording, so a technician can hold the pause
        // past the guard that caused it.
        var controls = HudControls.For(Capture(CaptureStates.Suppressed, reason));

        Assert.Equal([HudAction.Pause, HudAction.Stop], controls.Select(c => c.Action));
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.Finalizing)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftFailed)]
    [InlineData(CaptureStates.DraftPending)]
    [InlineData("a_state_from_a_newer_service")]
    public void NothingIsOfferedOutsideARunningSession(string state) =>
        Assert.Empty(HudControls.For(Capture(state)));

    [Fact]
    public void NothingIsOfferedWhenTheServiceIsNotHeardFrom() =>
        Assert.Empty(HudControls.For(null));

    /// <summary>Spec §5 S2: on hover or focus each control says its name and hotkey ("Pause · Ctrl+Alt+P").</summary>
    [Fact]
    public void EachTooltipNamesTheActionAndItsHotkey()
    {
        var recording = HudControls.For(Capture(CaptureStates.Recording)).ToDictionary(c => c.Action, c => c.Tooltip);
        var paused = HudControls.For(Capture(CaptureStates.Paused, CaptureReasons.User)).ToDictionary(c => c.Action, c => c.Tooltip);

        Assert.Equal("Mark moment · Ctrl+Alt+M", recording[HudAction.Mark]);
        Assert.Equal("Pause · Ctrl+Alt+P", recording[HudAction.Pause]);
        Assert.Equal("Stop and draft · Ctrl+Alt+S", recording[HudAction.Stop]);
        Assert.Equal("Resume · Ctrl+Alt+P", paused[HudAction.Resume]);
    }

    /// <summary>A screen reader hears the same words, the hotkey included, without the separator glyph.</summary>
    [Fact]
    public void EachControlIsNamedForAScreenReaderWithItsHotkey()
    {
        var pause = HudControls.For(Capture(CaptureStates.Recording)).Single(c => c.Action == HudAction.Pause);

        Assert.Equal("Pause", pause.Name);
        Assert.Equal("Ctrl+Alt+P", pause.Hotkey);
        Assert.Equal("Pause, Ctrl+Alt+P", pause.Spoken);
    }

    [Fact]
    public void NoCopyUsesAnEmDash()
    {
        var all = HudControls.For(Capture(CaptureStates.Recording))
            .Concat(HudControls.For(Capture(CaptureStates.Paused, CaptureReasons.User)));
        Assert.DoesNotContain(all, c => c.Tooltip.Contains('—', StringComparison.Ordinal) || c.Spoken.Contains('—', StringComparison.Ordinal));
    }

    private static CaptureStateSnapshot Capture(string state, string? reason = null) =>
        new() { State = state, SessionId = "s1", ElapsedMs = 1_000, Reason = reason };
}
