using ScreenTail.Core.Capabilities;
using ScreenTail.Core.History;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Onboarding;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// What the shell, Review, History and onboarding say about the state they are in (Spec §5, §7): the
/// status chip's tone, empty states that know a session is being captured, the reason beside a disabled
/// Discard, and a microphone fix that is one click.
/// </summary>
public sealed class StateCopyTests
{
    [Theory]
    [InlineData(CaptureStates.Recording, HudTone.Recording)]
    [InlineData(CaptureStates.Paused, HudTone.Paused)]
    [InlineData(CaptureStates.Suppressed, HudTone.Paused)]
    [InlineData(CaptureStates.Finalizing, HudTone.Paused)]
    [InlineData(CaptureStates.Idle, HudTone.Idle)]
    [InlineData(CaptureStates.DraftReady, HudTone.Idle)]
    [InlineData("new_state", HudTone.Idle)]
    public void TheStatusChipTakesTheToneThePillWouldShow(string state, HudTone tone)
    {
        // The chip's dot and border are the state's colour, so the shell and the pill agree at a glance.
        Assert.Equal(tone, ShellStatus.Tone(Snapshot(state)));
    }

    [Fact]
    public void AChipWithNobodyAnsweringIsNotCalledRecording()
    {
        Assert.Equal(HudTone.Idle, ShellStatus.Tone(Snapshot(CaptureStates.Recording) with { Connection = ServiceConnection.Unavailable }));
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    public void ReviewWithNothingToShowWhileCapturingSaysTheDraftIsComing(string state)
    {
        Assert.Equal("Capturing now: the draft opens here when you stop (Ctrl+Alt+S).", ShellStatus.NothingToReview(Snapshot(state)));
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.DraftReady)]
    public void ReviewWithNothingToShowBetweenSessionsSaysHowToStartOne(string state)
    {
        Assert.Equal("No draft to review yet. Record a session and stop it with Ctrl+Alt+S.", ShellStatus.NothingToReview(Snapshot(state)));
    }

    [Fact]
    public void ReviewWithNobodyAnsweringDoesNotClaimACapture()
    {
        var lost = Snapshot(CaptureStates.Recording) with { Connection = ServiceConnection.Unavailable };

        Assert.Equal(ShellStatus.NothingToReviewYet, ShellStatus.NothingToReview(lost));
    }

    [Fact]
    public void AnEmptyHistorySaysWhatToDoNext()
    {
        Assert.Equal("No sessions yet. Start a remote session and ScreenTail will capture it, or press Ctrl\u2060+\u2060Alt\u2060+\u2060R to start one now.", SessionHistory.Empty);
    }

    [Fact]
    public void DiscardWhileTheSessionIsCapturedSaysWhyBesideTheButton()
    {
        Assert.Equal("Stop the session to discard", SessionDiscard.Hint(SessionDiscard.StillCapturing));
        Assert.Null(SessionDiscard.Hint(null));

        // Not connected is said when the button is pressed, as before: there may be no session to stop.
        Assert.Null(SessionDiscard.Hint(ConfirmedAction.NotConnected));
    }

    [Fact]
    public void TheMicrophonesFixOpensItsPrivacyPage()
    {
        var row = new PermissionRow("microphone", "Microphone", false, "Microphone access is off.", CapabilityCopy.MicrophoneSettings);

        Assert.Equal("Open Windows privacy settings", row.FixLabel);
        Assert.Equal("Open Windows privacy settings: Microphone", row.FixName);
        Assert.Equal("Fix in Windows Settings", new PermissionRow("screen_capture", "Screen capture", false, "Blocked.", "ms-settings:privacy").FixLabel);
    }

    [Fact]
    public async Task AMicrophoneWindowsBlockedOffersItsPrivacyPageByName()
    {
        // "Microphone access is off" with only "Check again" left the technician to find the page alone.
        // The service's check carries the page; the button says where it goes. (A microphone the admin
        // blocked has no link and no button: AMicrophoneTheAdminBlockedOffersNoFixButton.)
        var wizard = Onboarding.OnboardingWizardTests.WizardFor(
        [
            CapabilityCopy.MicrophoneBlocked().ToWire(),
            new CapabilityStatus { Capability = "screen_capture", State = "ok", Message = "Screen capture works." },
            new CapabilityStatus { Capability = "input_hooks", State = "ok", Message = "Hooks work." },
        ]);

        await wizard.RecheckAsync();

        var mic = wizard.Permissions.Single(p => p.Capability == "microphone");
        Assert.Equal("ms-settings:privacy-microphone", mic.FixLink);
        Assert.Equal("Open Windows privacy settings", mic.FixLabel);
    }

    private static ShellSnapshot Snapshot(string state) =>
        new(ServiceConnection.Connected, new CaptureStateSnapshot { State = state, SessionId = "s1" }, ShellView.Review, Banner: null);
}
