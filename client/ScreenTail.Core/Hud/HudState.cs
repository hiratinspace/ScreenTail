using System.Globalization;
using ScreenTail.Core.Capabilities;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Hud;

/// <summary>Which design token colours the pill's left segment. Never the only signal (Spec §7).</summary>
public enum HudTone
{
    /// <summary>Capture is running. <c>state.recording</c>.</summary>
    Recording,

    /// <summary>Capture is stopped and will resume. <c>state.paused</c>.</summary>
    Paused,

    /// <summary>In a session, deliberately not capturing this window. <c>state.scope</c>.</summary>
    Scope,

    /// <summary>Nothing is wrong and nothing is happening. <c>text.muted</c>.</summary>
    Idle,
}

/// <param name="Glyph">Spec §7: the shape carries the state as well as the colour does.</param>
/// <param name="Text">The left segment, in the technician's words.</param>
/// <param name="Tooltip">Longer wording on hover; null when the text says everything.</param>
public sealed record HudSegment(HudTone Tone, string Glyph, string Text, string? Tooltip = null);

/// <param name="Mic">Null when a microphone is present and working — the meter is shown instead.</param>
/// <param name="Cloud">Null when online. Spec §5 S2's grey cloud otherwise.</param>
public sealed record HudSnapshot(
    HudSegment State,
    HudSegment? Mic,
    HudSegment? Cloud,
    int PendingRedactions,
    bool Visible);

/// <summary>
/// What the recording pill says (ST-072, Spec §5 S2).
///
/// Derived from the service's own state, the capability probe and connectivity — never stored, and never
/// guessed at by the UI. INV-4 makes this the most safety-critical string in the product: it is the thing
/// a technician looks at to know whether a customer's screen is being recorded, and the one place where
/// being out of date is indistinguishable from lying.
///
/// So when the pieces disagree, this errs towards saying capture is happening. A HUD that wrongly says
/// "recording" costs a moment of confusion; one that wrongly says "paused" is the silent-capture path the
/// whole design forbids.
/// </summary>
public static class HudState
{
    /// <param name="capture">The last thing the service said. Null when the UI has never heard from it.</param>
    /// <param name="capabilities">What Windows currently allows (ST-021).</param>
    /// <param name="online">Whether a cloud draft could be requested right now.</param>
    /// <param name="hidden">The technician right-clicked the pill away for this session.</param>
    public static HudSnapshot For(
        CaptureStateSnapshot? capture,
        CapabilityReport? capabilities = null,
        bool online = true,
        bool hidden = false)
    {
        var state = Segment(capture);

        return new HudSnapshot(
            state,
            Mic(capabilities),
            online ? null : new HudSegment(HudTone.Idle, "☁", "Offline", "Offline — draft will be created when connected."),
            capture?.PendingRedactions ?? 0,

            // Hidden only ever hides the pill, and only while nothing is being captured — Spec v0.4.1 Q1
            // settled that the HUD stays up during screen sharing, because an auto-hiding indicator is a
            // silent-capture path wearing a convenience's clothes. The tray icon remains either way.
            Visible: !hidden || state.Tone is HudTone.Recording or HudTone.Paused or HudTone.Scope);
    }

    /// <summary>mm:ss, or h:mm:ss once a session runs past the hour.</summary>
    public static string Elapsed(long? milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds ?? 0));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }

    private static HudSegment Segment(CaptureStateSnapshot? capture)
    {
        if (capture is null)
        {
            // The UI has not heard from the service. It cannot say capture is off, because it does not
            // know — and "unknown" is the honest word for the state INV-4 cares most about.
            return new HudSegment(
                HudTone.Idle,
                "?",
                "Capture state unknown",
                "Not connected to the capture service. It may still be recording.");
        }

        return capture.State switch
        {
            CaptureStates.Recording => new HudSegment(HudTone.Recording, "●", Elapsed(capture.ElapsedMs)),

            CaptureStates.Paused => Paused(capture),

            // Suppression is never the technician's doing, so it always says what caused it: they have not
            // pressed anything, and a pill that just says "paused" invites them to look for what they did.
            CaptureStates.Suppressed => Suppressed(capture),

            CaptureStates.Finalizing => new HudSegment(HudTone.Paused, "⋯", "Finishing up"),

            _ => new HudSegment(HudTone.Idle, "○", "Not recording"),
        };
    }

    private static HudSegment Paused(CaptureStateSnapshot capture) =>
        capture.Reason is CaptureReasons.User or null
            ? new HudSegment(HudTone.Paused, "‖", "Paused — press Ctrl+Alt+P to resume")
            : Suppressed(capture);

    private static HudSegment Suppressed(CaptureStateSnapshot capture) => capture.Reason switch
    {
        CaptureReasons.PasswordField => new HudSegment(HudTone.Paused, "⏸", "Paused: sensitive field"),
        CaptureReasons.SensitiveContext => new HudSegment(HudTone.Paused, "⏸", "Paused: sensitive field"),
        CaptureReasons.ExcludedApp => new HudSegment(HudTone.Paused, "⏸", "Paused: excluded app"),

        CaptureReasons.ElevatedWindow => new HudSegment(
            HudTone.Paused,
            "⏸",
            "Paused: elevated window",

            // Spec v0.4.1 Q5 fixes this wording. It explains a limitation rather than implying a choice:
            // Windows will not let us capture an elevated window, and the technician cannot fix that.
            "Elevated window — screen not captured."),

        CaptureReasons.OutOfScope => new HudSegment(
            HudTone.Scope,
            "◎",
            capture.ScopeProcess is { Length: > 0 } process
                ? $"Not capturing — {process}"
                : "Not capturing — out of scope",
            "Clicks are still on the timeline; no screenshots are taken."),

        _ => new HudSegment(HudTone.Paused, "⏸", "Paused"),
    };

    private static HudSegment? Mic(CapabilityReport? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        var microphone = capabilities[Capability.Microphone];
        return microphone.State is CapabilityState.Ok
            ? null
            : new HudSegment(HudTone.Idle, "🎤", "No mic", "No microphone. Capture continues without voice.");
    }
}

/// <summary>
/// The suppression reasons as they appear on the wire, matching <c>CaptureStateReason</c> in
/// session.v1.json. Constants rather than the generated enum, because the IPC snapshot carries a string
/// and an unrecognised one has to fall through to something sensible rather than throw at a technician.
/// </summary>
public static class CaptureReasons
{
    public const string User = "user";
    public const string PasswordField = "password_field";
    public const string ExcludedApp = "excluded_app";
    public const string ElevatedWindow = "elevated_window";
    public const string SensitiveContext = "sensitive_context";
    public const string OutOfScope = "out_of_scope";
}
