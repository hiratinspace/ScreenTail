namespace ScreenTail.Core.Capabilities;

/// <summary>The things capture needs from Windows, each of which a user or an admin can take away.</summary>
public enum Capability
{
    /// <summary>An interactive desktop. Without one, hooks never fire and captures come back black (ADR-0003).</summary>
    DesktopSession,

    /// <summary>Windows microphone privacy, per user and per machine. Technician audio only (INV-9).</summary>
    Microphone,

    /// <summary>Taking a screenshot of the desktop at all.</summary>
    ScreenCapture,

    /// <summary>Installing the low-level input hooks that drive click and scene detection (INV-2: no keystrokes).</summary>
    InputHooks,

    /// <summary>Reading windows owned by elevated processes. Blind here is normal and not an error.</summary>
    ElevatedWindows,
}

public enum CapabilityState
{
    /// <summary>Works.</summary>
    Ok,

    /// <summary>Usable, but something is missing and the technician should know what (Spec §6).</summary>
    Degraded,

    /// <summary>Capture cannot do this at all.</summary>
    Blocked,

    /// <summary>The check itself could not run. Treated as blocked; reported differently so the cause isn't hidden.</summary>
    Unknown,
}

/// <param name="Message">Sentence case, no exclamation marks, names the object (Spec §6 copy rules).</param>
/// <param name="FixHint">What the technician should do. Null when there's nothing for them to do.</param>
/// <param name="FixLink">A Windows Settings deep link the HUD's "Fix" button opens (ST-072). Null when none applies.</param>
public sealed record CapabilityCheck(
    Capability Capability,
    CapabilityState State,
    string Message,
    string? FixHint = null,
    string? FixLink = null)
{
    public bool Blocks => State is CapabilityState.Blocked or CapabilityState.Unknown;
}

/// <param name="CheckedAt">When the probe ran. Capabilities change while the service is running, so this is not a one-off.</param>
public sealed record CapabilityReport(DateTimeOffset CheckedAt, IReadOnlyList<CapabilityCheck> Checks)
{
    /// <summary>Whether capture can run at all: a desktop session, screenshots and hooks are the hard requirements.</summary>
    public bool CanCapture =>
        !Blocked(Capability.DesktopSession) && !Blocked(Capability.ScreenCapture) && !Blocked(Capability.InputHooks);

    /// <summary>What the HUD shows: the first thing standing in the technician's way, or nothing.</summary>
    public CapabilityCheck? MostImportantProblem =>
        Checks.Where(c => c.State != CapabilityState.Ok)
            .OrderBy(c => c.State == CapabilityState.Degraded ? 1 : 0)
            .ThenBy(c => (int)c.Capability)
            .FirstOrDefault();

    public CapabilityCheck this[Capability capability] => Checks.Single(c => c.Capability == capability);

    private bool Blocked(Capability capability) =>
        Checks.FirstOrDefault(c => c.Capability == capability)?.Blocks ?? true;
}

/// <summary>
/// The words the technician sees (Spec §6). They live here, beside the states, so the copy is reviewable in
/// one place and the HUD (ST-072) and the diagnostics panel can't drift apart.
/// </summary>
public static class CapabilityCopy
{
    public const string MicrophoneSettings = "ms-settings:privacy-microphone";

    public static CapabilityCheck Ok(Capability capability, string message) =>
        new(capability, CapabilityState.Ok, message);

    public static CapabilityCheck MicrophoneBlocked() => new(
        Capability.Microphone,
        CapabilityState.Blocked,
        "Microphone is blocked by Windows privacy settings.",
        "Allow desktop apps to use your microphone, then start the session again.",
        MicrophoneSettings);

    public static CapabilityCheck MicrophoneBlockedByAdmin() => new(
        Capability.Microphone,
        CapabilityState.Blocked,
        "Microphone is blocked by your organization's policy.",
        "Your admin controls this setting. Sessions will be captured without narration.");

    public static CapabilityCheck NoMicrophone() => new(
        Capability.Microphone,
        CapabilityState.Degraded,
        "No microphone found.",
        "Plug in a microphone or headset to narrate what you're doing.");

    public static CapabilityCheck ElevatedWindowsInvisible() => new(
        Capability.ElevatedWindows,
        CapabilityState.Degraded,
        "Elevated window — screen not captured.",
        "Windows hides elevated windows from ScreenTail. Say what you did and it goes in the note.");

    public static CapabilityCheck NoDesktopSession() => new(
        Capability.DesktopSession,
        CapabilityState.Blocked,
        "ScreenTail isn't running in your desktop session, so it can't capture anything.",
        "Sign in and start ScreenTail from the Start menu.");

    public static CapabilityCheck ScreenCaptureBlocked(string reason) => new(
        Capability.ScreenCapture,
        CapabilityState.Blocked,
        "Screenshots are coming back empty.",
        $"Capture returned nothing ({reason}). Check display drivers or remote-session settings.");

    public static CapabilityCheck HooksBlocked(string reason) => new(
        Capability.InputHooks,
        CapabilityState.Blocked,
        "ScreenTail can't see clicks, so it can't tell which steps matter.",
        $"Installing the input hook failed ({reason}). Security software often blocks this.");

    public static CapabilityCheck Failed(Capability capability, string reason) => new(
        capability,
        CapabilityState.Unknown,
        "ScreenTail couldn't check this.",
        reason);
}
