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
        Assert.Equal("Paused: press Ctrl+Alt+P to resume", hud.State.Text);
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

        Assert.Equal("Elevated window: screen not captured.", hud.State.Tooltip);
    }

    [Fact]
    public void OutOfScopeNamesTheWindowInFront()
    {
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: CaptureReasons.OutOfScope, process: "OUTLOOK"));

        Assert.Equal(HudTone.Scope, hud.State.Tone);
        Assert.Equal("Not capturing: OUTLOOK", hud.State.Text);
    }

    [Fact]
    public void OutOfScopeWithNoProcessStillSaysItIsNotCapturing()
    {
        // The process name is the nicety; "not capturing" is the part INV-4 requires, and it must survive
        // the service not knowing what is in front.
        var hud = HudState.For(Capture(CaptureStates.Suppressed, reason: CaptureReasons.OutOfScope));

        Assert.Equal("Not capturing: out of scope", hud.State.Text);
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
        Assert.Equal("Offline: draft will be created when connected.", hud.Cloud.Tooltip);
        Assert.Equal(HudTone.Recording, hud.State.Tone);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    [InlineData("recording_with_audio")]
    public void HidingThePillDoesNotWorkWhileThereIsSomethingToIndicate(string state)
    {
        // Spec v0.4.1 Q1: the HUD stays up, including while the technician shares their screen, because an
        // indicator that can be dismissed mid-session is a silent-capture path with a convenience's face
        // on it. Right-click hides it between sessions; the tray icon remains either way. Finishing up is
        // still a session, and a state this build has never heard of may be one (2026-10-12 review, T-138).
        Assert.True(HudState.For(Capture(state), hidden: true).Visible);
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftFailed)]
    public void HidingThePillWorksWhenNothingIsBeingCaptured(string state)
    {
        // A session that has ended rests in draft_ready or draft_failed, not idle. Both are the service
        // saying it is not capturing, so both may be put away (2026-10-12 review, S-39).
        Assert.False(HudState.For(Capture(state), hidden: true).Visible);
    }

    [Theory]
    [InlineData(CaptureStates.DraftReady, "Not recording: draft ready")]
    [InlineData(CaptureStates.DraftFailed, "Not recording: draft failed")]
    public void AFinishedSessionSaysItIsNotRecording(string state, string expected)
    {
        // After every session the pill used to say "Capture state unknown, it may be recording", which
        // was false and alarming in equal measure (S-39, T-137).
        var hud = HudState.For(Capture(state));

        Assert.Equal(expected, hud.State.Text);
        Assert.Equal(HudTone.Idle, hud.State.Tone);
        Assert.DoesNotContain("unknown", hud.State.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EveryStateTheServiceSends))]
    public void EveryStateTheServiceSendsHasItsOwnWords(string state)
    {
        // Read off CaptureStates by reflection, so a state added to the contract without words here
        // fails this rather than falling through to "unknown" in front of a technician (T-137).
        var hud = HudState.For(Capture(state));

        Assert.DoesNotContain("unknown", hud.State.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("?", hud.State.Glyph);
    }

    public static TheoryData<string> EveryStateTheServiceSends
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var constant in typeof(CaptureStates).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (constant.IsLiteral && constant.GetRawConstantValue() is string state)
                {
                    data.Add(state);
                }
            }

            return data;
        }
    }

    [Fact]
    public void TheReflectionTheoryReadsEveryStateOffTheContract()
    {
        // The control for the theory above: a contract with its constants renamed would give it nothing
        // to check and it would pass vacuously.
        Assert.Equal(7, EveryStateTheServiceSends.Count);
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftFailed)]
    public void HidingThePillBetweenSessionsDoesNotStopTheNextOneStarting(string state)
    {
        // The service refuses to start a session nothing indicates, and asks before it starts. A pill the
        // technician put away between sessions used to stop the UI reporting, so every later start, by
        // hotkey or by a remote tool taking the foreground, was refused until the UI restarted (T-154).
        // Put away is tray-only by choice, which INV-4 allows, and the tray icon is still there to report.
        var hud = HudState.For(Capture(state), hidden: true);

        Assert.False(hud.Visible);
        Assert.True(HudState.Indicates(hud, pillOnScreen: false));
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Idle)]
    public void APillOffEveryScreenIsNotReportedAsShowing(string state)
    {
        // A pill restored onto a monitor that has since been unplugged is drawn into empty coordinate
        // space. It is up, and it indicates nothing (ST-072, S-42).
        var hud = HudState.For(Capture(state));

        Assert.True(hud.Visible);
        Assert.False(HudState.Indicates(hud, pillOnScreen: false));
        Assert.True(HudState.Indicates(hud, pillOnScreen: true));
    }

    [Fact]
    public void AnUnknownStateIsReportedOnlyWhileThePillIsOnScreen()
    {
        // Hidden or not, unknown keeps the pill up, so the claim is about the pill and not about the tray.
        var hud = HudState.For(capture: null, hidden: true);

        Assert.True(hud.Visible);
        Assert.False(HudState.Indicates(hud, pillOnScreen: false));
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    public void ASessionUnderWayIsNamedSoTheShellCanClearAHideMeantForTheLastOne(string state)
    {
        // Hiding is for one session and no longer (Spec §5 S2). The tray's item used to hide the pill for
        // the life of the process (T-154); the shell clears the wish when a session it has not seen starts.
        Assert.Equal("s2", HudState.SessionUnderWay(new CaptureStateSnapshot { State = state, SessionId = "s2" }));
    }

    [Theory]
    [InlineData(CaptureStates.Idle, "s1")]
    [InlineData(CaptureStates.Finalizing, "s1")]
    [InlineData(CaptureStates.DraftReady, "s1")]
    [InlineData(CaptureStates.DraftFailed, "s1")]
    [InlineData("recording_with_audio", "s1")]
    [InlineData(CaptureStates.Recording, null)]
    [InlineData(CaptureStates.Recording, "")]
    public void NothingIsUnderWayBetweenSessionsOrWhenItCannotBeTold(string state, string? session)
    {
        Assert.Null(HudState.SessionUnderWay(new CaptureStateSnapshot { State = state, SessionId = session }));
        Assert.Null(HudState.SessionUnderWay(null));
    }

    [Fact]
    public void HidingThePillDoesNotWorkWhileTheStateIsUnknown()
    {
        // ST-048 (weaknesses P0-3). Hiding is permitted because between sessions there is nothing to
        // indicate. When the UI has not heard from the service it does not know that — the pipe may have
        // dropped mid-session, or the service may have restarted while recording — and every other line
        // of this class says so out loud ("it may still be recording"). Hiding the pill in the one state
        // the class refuses to guess at would take that sentence off the screen, which is the
        // silent-capture path INV-4 exists to forbid. Unknown is treated as something to indicate.
        Assert.True(HudState.For(capture: null, hidden: true).Visible);
    }

    [Fact]
    public void AnUnknownStateSaysSoRatherThanSayingIdle()
    {
        var hud = HudState.For(capture: null, hidden: true);

        Assert.Equal("Capture state unknown", hud.State.Text);
        Assert.Equal("Not connected to the capture service. It may still be recording.", hud.State.Tooltip);
    }

    [Fact]
    public void TheRedactionCountIsWhateverTheServiceSaid()
    {
        Assert.Equal(3, HudState.For(Capture(CaptureStates.Recording, pending: 3)).PendingRedactions);
    }

    [Fact]
    public void NoPillStringCarriesAnEmDash()
    {
        // The pill's words use plain punctuation, as the tray's and the shell's do (2026-10-12 review).
        // Every state and every pause reason, with and without a process, offline, with each microphone.
        string[] states = [CaptureStates.Idle, CaptureStates.Recording, CaptureStates.Paused, CaptureStates.Suppressed, CaptureStates.Finalizing, CaptureStates.DraftReady, CaptureStates.DraftFailed, "new_state"];
        string?[] reasons = [null, CaptureReasons.User, CaptureReasons.PasswordField, CaptureReasons.SensitiveContext, CaptureReasons.ExcludedApp, CaptureReasons.ElevatedWindow, CaptureReasons.OutOfScope, "new_reason"];
        var said = new List<string>();
        foreach (var state in states)
        {
            foreach (var reason in reasons)
            {
                foreach (var process in new string?[] { null, "OUTLOOK" })
                {
                    foreach (var capabilities in new CapabilityReport?[] { null, Capabilities(CapabilityState.Ok), Capabilities(CapabilityState.Blocked) })
                    {
                        Collect(HudState.For(Capture(state, 1_000, reason, process), capabilities, online: false), said);
                    }
                }
            }
        }

        Collect(HudState.For(null, online: false), said);

        Assert.NotEmpty(said);
        Assert.All(said, text => Assert.DoesNotContain("—", text, StringComparison.Ordinal));
    }

    private static void Collect(HudSnapshot hud, List<string> said)
    {
        foreach (var segment in new[] { hud.State, hud.Mic, hud.Cloud })
        {
            if (segment is not null)
            {
                said.Add(segment.Text);
                said.Add(segment.Tooltip ?? string.Empty);
            }
        }
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
