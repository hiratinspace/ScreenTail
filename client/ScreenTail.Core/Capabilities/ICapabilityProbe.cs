namespace ScreenTail.Core.Capabilities;

/// <summary>
/// Asks Windows what it will and won't allow (ST-021). Run at service start, before each session, and on
/// demand from the UI: a technician can revoke microphone access halfway through a working day.
/// </summary>
public interface ICapabilityProbe
{
    CapabilityReport Probe();
}

/// <summary>A probe that reports everything working. Used by tests and by the macOS build, which has no capture.</summary>
public sealed class AlwaysCapableProbe(TimeProvider? time = null) : ICapabilityProbe
{
    public CapabilityReport Probe() => new(
        (time ?? TimeProvider.System).GetUtcNow(),
        [
            CapabilityCopy.Ok(Capability.DesktopSession, "Running in your desktop session."),
            CapabilityCopy.Ok(Capability.Microphone, "Microphone is available."),
            CapabilityCopy.Ok(Capability.ScreenCapture, "Screenshots are working."),
            CapabilityCopy.Ok(Capability.InputHooks, "Clicks and scene changes are being detected."),
            CapabilityCopy.Ok(Capability.ElevatedWindows, "Elevated windows are visible."),
        ]);
}
