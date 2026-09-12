using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Capabilities;

/// <summary>Turns a report into its wire form. Names match <c>docs/ipc-contract.md</c>; snake_case like the rest.</summary>
public static class CapabilityWire
{
    public static CapabilitiesReported ToWire(this CapabilityReport report, int? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new CapabilitiesReported
        {
            RequestId = requestId,
            CheckedAt = report.CheckedAt,
            CanCapture = report.CanCapture,
            Checks = [.. report.Checks.Select(ToWire)],
        };
    }

    public static CapabilityStatus ToWire(this CapabilityCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new CapabilityStatus
        {
            Capability = Name(check.Capability),
            State = Name(check.State),
            Message = check.Message,
            FixHint = check.FixHint,
            FixLink = check.FixLink,
        };
    }

    public static string Name(Capability capability) => capability switch
    {
        Capability.DesktopSession => "desktop_session",
        Capability.Microphone => "microphone",
        Capability.ScreenCapture => "screen_capture",
        Capability.InputHooks => "input_hooks",
        Capability.ElevatedWindows => "elevated_windows",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
    };

    public static string Name(CapabilityState state) => state switch
    {
        CapabilityState.Ok => "ok",
        CapabilityState.Degraded => "degraded",
        CapabilityState.Blocked => "blocked",
        CapabilityState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
