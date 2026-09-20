using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-071: the tray icon and the "What's being captured right now?" panel.
///
/// The tray icon is the one indicator that is always on screen, so INV-3 and INV-4 both rest on it saying
/// the right thing in one 16-pixel glyph. The panel exists to answer a customer standing behind a
/// technician asking what that is recording — which is also why nothing it shows may be content.
/// </summary>
public sealed class TrayAndDiagnosticsTests
{
    [Fact]
    public void RecordingLooksDifferentFromPausedAndFromUnknown()
    {
        // Three states a technician has to tell apart at a glance, and the only place they can.
        Assert.Equal(TrayIcon.Recording, Presence("recording").Icon);
        Assert.Equal(TrayIcon.Paused, Presence("paused").Icon);
        Assert.Equal(TrayIcon.Paused, Presence("suppressed").Icon);
        Assert.Equal(TrayIcon.Idle, Presence("idle").Icon);
    }

    [Fact]
    public void TheTooltipSaysWhatAndForHowLong()
    {
        // Spec §5 S1's wording. "Recording" alone does not tell a technician whether the thing they
        // started an hour ago is still going.
        Assert.Equal("ScreenTail — Recording — screenconnect (12:41)", Presence("recording").Tooltip);
    }

    [Fact]
    public void LosingTheServiceIsItsOwnIconAndNotIdle()
    {
        // A grey icon says "ask me again"; an idle icon says "nothing is being captured". Only one of
        // those is honest when the service has stopped answering, and INV-4 turns on the difference.
        var offline = TrayPresence.From(new ShellSnapshot(ServiceConnection.Unavailable, Recording(), ShellView.Review, "banner"));

        Assert.Equal(TrayIcon.Offline, offline.Icon);
        Assert.DoesNotContain("Recording", offline.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftsReadyShowTheirCount()
    {
        var presence = TrayPresence.From(new ShellSnapshot(
            ServiceConnection.Connected,
            new CaptureStateSnapshot { State = "draft_ready", DraftsReady = 2 },
            ShellView.Review,
            null));

        Assert.Equal(TrayIcon.DraftReady, presence.Icon);
        Assert.Equal(2, presence.DraftsReady);
    }

    [Fact]
    public void DraftsWaitingAreVisibleEvenWhenNothingIsRunning()
    {
        // The session ended hours ago and two notes are still unpublished. An idle icon would let them sit
        // there unnoticed, which is the failure the badge exists for.
        var presence = TrayPresence.From(new ShellSnapshot(
            ServiceConnection.Connected,
            new CaptureStateSnapshot { State = "idle", DraftsReady = 3 },
            ShellView.Review,
            null));

        Assert.Equal(TrayIcon.DraftReady, presence.Icon);
    }

    [Fact]
    public void TheTooltipNeverCarriesAWindowTitle()
    {
        // The tray tooltip is on screen during a screen share. The remote tool's name is the answer;
        // what the technician has open in it is the customer's business (INV-10).
        var presence = Presence("recording");

        Assert.DoesNotContain("—  ", presence.Tooltip, StringComparison.Ordinal);
        Assert.Equal(presence.Tooltip, $"ScreenTail — {presence.StateLine}");
    }

    [Fact]
    public void CopiedDiagnosticsCarryStatesAndCountsAndNothingElse()
    {
        // AC3. This text goes into a ticket or an email, so a window title or a line of OCR reaching it is
        // the same leak as one in the audit log.
        var text = Sample().ToClipboardText(new DateTimeOffset(2026, 9, 13, 17, 0, 0, TimeSpan.Zero));

        Assert.Contains("redaction queue  4", text, StringComparison.Ordinal);
        Assert.Contains("keystrokes held  17", text, StringComparison.Ordinal);
        Assert.Contains("egress blocked   2", text, StringComparison.Ordinal);
        Assert.Contains("local-only       yes", text, StringComparison.Ordinal);
        Assert.Contains("Not capturing — outlook", text, StringComparison.Ordinal);

        // And nothing that could only have come from the screen or the microphone.
        Assert.DoesNotContain("Acme", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDiagnosticsTypeHasNowhereToPutContent()
    {
        // The argument is structural, not a habit: the panel is built from this record alone, and every
        // one of its fields is a state, a count, or a name a technician could read off the registry.
        var content = typeof(Diagnostics).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["Scope", "Microphone", "Suppression", "PolicyVersion", "ClientVersion", "Headline"], content);
    }

    [Fact]
    public void ASuppressedSessionSaysSoFirst()
    {
        // What the customer over the shoulder needs to read: not the scope, but that it has stopped.
        var suppressed = Sample() with { Suppression = "a password field has focus" };

        Assert.Equal("Paused — a password field has focus", suppressed.Headline);
    }

    private static Diagnostics Sample() => new(
        Scope: "Not capturing — outlook",
        Microphone: "Headset Microphone (Realtek)",
        Suppression: null,
        RedactionBacklog: 4,
        FramesDropped: 1,
        KeystrokesDropped: 17,
        EgressBlocked: 2,
        LocalOnly: true,
        PolicyVersion: "policy-2026-09-13",
        CpuPercent: 3.4,
        WorkingSetBytes: 240L * 1024 * 1024,
        ClientVersion: "0.1.0");

    private static TrayPresence Presence(string state) => TrayPresence.From(new ShellSnapshot(
        ServiceConnection.Connected,
        state == "idle" ? new CaptureStateSnapshot { State = "idle" } : Recording(state),
        ShellView.Review,
        null));

    private static CaptureStateSnapshot Recording(string state = "recording") => new()
    {
        State = state,
        SessionId = "s1",
        RemoteTool = "screenconnect",
        ElapsedMs = (12 * 60 * 1000) + 41_000,
    };

    [Fact]
    public void AStateThisBuildHasNeverHeardOfIsUnknownRatherThanIdle()
    {
        // 2026-09-19 review. An unrecognised state fell through to "Not capturing" and the idle ring, so
        // a service newer than this UI — one that had added a recording-like state — would show a
        // technician the icon that means nothing is happening. That is the one guess INV-4 cannot afford
        // to get wrong, and KnownIdle already says so about whether the pill is shown; this is the same
        // reasoning applied to what it says.
        var presence = TrayPresence.From(new ShellSnapshot(
            ServiceConnection.Connected,
            new CaptureStateSnapshot { State = "recording_with_audio" },
            ShellView.Review,
            null));

        Assert.Equal(TrayIcon.Offline, presence.Icon);
        Assert.Contains("unknown", presence.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdleStillLooksIdle()
    {
        // The control: a change that made everything unknown would pass the test above and would put a
        // warning glyph in the tray all day.
        var presence = TrayPresence.From(new ShellSnapshot(
            ServiceConnection.Connected,
            new CaptureStateSnapshot { State = CaptureStates.Idle },
            ShellView.Review,
            null));

        Assert.Equal(TrayIcon.Idle, presence.Icon);
    }

    [Fact]
    public void ThePillSaysUnknownForAStateItDoesNotKnow()
    {
        var hud = Core.Hud.HudState.For(new CaptureStateSnapshot { State = "recording_with_audio" });

        Assert.Contains("unknown", hud.State.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(hud.Visible);
    }
}
