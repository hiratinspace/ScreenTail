using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Hud;

/// <summary>
/// ST-072: what the recording pill says (Spec §5 S2).
///
/// INV-4 makes this the most safety-critical string in the product. It is what a technician looks at to
/// know whether a customer's screen is being recorded, and the only place where being out of date is
/// indistinguishable from lying.
/// </summary>
public sealed class HudStateTests
{
    [Fact]
    public void RecordingShowsTheTimer()
    {
        var hud = HudState.For(Capture(CaptureStates.Recording, elapsedMs: 761_000));

        Assert.Equal(HudTone.Recording, hud.State.Tone);
        Assert.Equal("12:41", hud.State.Text);
        Assert.Equal("●", hud.State.Glyph);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59_999, "00:59")]
    [InlineData(60_000, "01:00")]
    [InlineData(3_599_000, "59:59")]
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(7_265_000, "2:01:05")]
    public void TheTimerReadsAsAClockAtEveryLength(long elapsed, string expected)
    {
        // A remote session can run for hours. "121:05" is not a time anybody reads.
        Assert.Equal(expected, HudState.Elapsed(elapsed));
    }

    [Fact]
    public void ANegativeOrMissingElapsedIsZeroRatherThanNonsense()
    {
        // The service sends null before the first tick, and a clock that ran backwards would be a stranger
        // sight than a zero.
        Assert.Equal("00:00", HudState.Elapsed(null));
        Assert.Equal("00:00", HudState.Elapsed(-5_000));
    }

    [Fact]
    public void APauseTheTechnicianCausedSaysHowToUndoIt()
    {
        var hud = HudState.For(Capture(CaptureStates.Paused, reason: CaptureReasons.User));

        Assert.Equal(HudTone.Paused, hud.State.Tone);
        Assert.Equal("Paused — press Ctrl+Alt+P to resume", hud.State.Text);
    }

    [Theory]
    [InlineData(CaptureReasons.PasswordField, "Paused: sensitive field")]
    [InlineData(CaptureReasons.SensitiveContext, "Paused: sensitive field")]
    [InlineData(CaptureReasons.ExcludedApp, "Paused: excluded app")]
    [InlineData(CaptureReasons.ElevatedWindow, "Paused: elevated window")]
    public void ASuppressionSaysWhatCausedIt(string reason, string expected)
    {
        // The technician pressed nothing. A pill that just says "paused" sends them looking for what they
        // did, and Spec §5 S2 gives each cause its own words for that reason.
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: reason));

        Assert.Equal(expected, hud.State.Text);
        Assert.Equal(HudTone.Paused, hud.State.Tone);
    }

    [Fact]
    public void TheElevatedWindowTooltipIsTheWordingTheOwnerChose()
    {
        // Spec v0.4.1 Q5, verbatim. It explains a limitation rather than implying a choice: Windows will
        // not let us capture an elevated window and the technician cannot fix that.
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: CaptureReasons.ElevatedWindow));

        Assert.Equal("Elevated window — screen not captured.", hud.State.Tooltip);
    }

    [Fact]
    public void OutOfScopeNamesTheWindowInFront()
    {
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: CaptureReasons.OutOfScope, process: "OUTLOOK"));

        Assert.Equal(HudTone.Scope, hud.State.Tone);
        Assert.Equal("Not capturing — OUTLOOK", hud.State.Text);
    }

    [Fact]
    public void OutOfScopeWithNoProcessStillSaysItIsNotCapturing()
    {
        // The process name is the nicety; "not capturing" is the part INV-4 requires, and it must survive
        // the service not knowing what is in front.
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: CaptureReasons.OutOfScope));

        Assert.Equal("Not capturing — out of scope", hud.State.Text);
        Assert.Equal(HudTone.Scope, hud.State.Tone);
    }

    [Fact]
    public void AReasonWeDoNotRecogniseStillReadsAsStopped()
    {
        // A newer service, or a reason added later. Falling through to "recording" would be the one
        // failure this class exists to prevent; falling through to "paused" is merely vague.
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: "something_new"));

        Assert.NotEqual(HudTone.Recording, hud.State.Tone);
        Assert.Equal("Paused", hud.State.Text);
    }

    [Fact]
    public void NoWordFromTheServiceIsSaidOutLoudRatherThanShownAsIdle()
    {
        // The dangerous default. If the UI loses the pipe it does not know capture stopped - the service
        // owns that state (ADR-0003) - and showing "not recording" would be a silent-capture path opened
        // by a dropped connection.
        var hud = HudState.For(capture: null);

        Assert.NotEqual(HudTone.Recording, hud.State.Tone);
        Assert.Equal("Capture state unknown", hud.State.Text);
        Assert.Contains("may still be recording", hud.State.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingMicrophoneIsShownWithoutStoppingCapture()
    {
        var hud = HudState.For(Capture(CaptureStates.Recording), Capabilities(CapabilityState.Blocked));

        Assert.NotNull(hud.Mic);
        Assert.Equal("No microphone. Capture continues without voice.", hud.Mic.Tooltip);
        Assert.Equal(HudTone.Recording, hud.State.Tone);
    }

    [Fact]
    public void AWorkingMicrophoneShowsTheMeterInsteadOfAWarning()
    {
        Assert.Null(HudState.For(Capture(CaptureStates.Recording), Capabilities(CapabilityState.Ok)).Mic);
    }

    [Fact]
    public void BeingOfflineIsSaidOnThePillAndDoesNotStopAnything()
    {
        var hud = HudState.For(Capture(CaptureStates.Recording), online: false);

        Assert.NotNull(hud.Cloud);
        Assert.Equal("Offline — draft will be created when connected.", hud.Cloud.Tooltip);
        Assert.Equal(HudTone.Recording, hud.State.Tone);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    public void HidingThePillDoesNotWorkWhileThereIsSomethingToIndicate(string state)
    {
        // Spec v0.4.1 Q1: the HUD stays up, including while the technician shares their screen, because an
        // indicator that can be dismissed mid-session is a silent-capture path with a convenience's face
        // on it. Right-click hides it between sessions; the tray icon remains either way.
        Assert.True(HudState.For(Capture(state), hidden: true).Visible);
    }

    [Fact]
    public void HidingThePillWorksWhenNothingIsBeingCaptured()
    {
        Assert.False(HudState.For(Capture(CaptureStates.Idle), hidden: true).Visible);
    }

    [Fact]
    public void TheRedactionCountIsWhateverTheServiceSaid()
    {
        Assert.Equal(3, HudState.For(Capture(CaptureStates.Recording, pending: 3)).PendingRedactions);
    }

    private static CaptureStateSnapshot Capture(
        string state,
        long? elapsedMs = null,
        string? reason = null,
        string? process = null,
        int pending = 0) => new()
        {
            State = state,
            SessionId = "s1",
            ElapsedMs = elapsedMs,
            Reason = reason,
            ScopeProcess = process,
            PendingRedactions = pending,
        };

    private static CapabilityReport Capabilities(CapabilityState microphone) => new(
        DateTimeOffset.UnixEpoch,
        [
            CapabilityCopy.Ok(Capability.DesktopSession, "ok"),
            microphone is CapabilityState.Ok
                ? CapabilityCopy.Ok(Capability.Microphone, "ok")
                : CapabilityCopy.NoMicrophone(),
            CapabilityCopy.Ok(Capability.ScreenCapture, "ok"),
            CapabilityCopy.Ok(Capability.InputHooks, "ok"),
            CapabilityCopy.Ok(Capability.ElevatedWindows, "ok"),
        ]);
}
