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

    /// <summary>
    /// A report back from its wire form, for the UI's pill (ST-072, T-372). A capability this build does
    /// not know is left out rather than guessed at; a state it does not know reads as unknown, which the
    /// report already treats as blocked.
    /// </summary>
    public static CapabilityReport FromWire(this CapabilitiesReported reported)
    {
        ArgumentNullException.ThrowIfNull(reported);
        var checks = new List<CapabilityCheck>(reported.Checks.Count);
        foreach (var status in reported.Checks)
        {
            if (CapabilityNamed(status.Capability) is { } capability)
            {
                checks.Add(new CapabilityCheck(capability, StateNamed(status.State), status.Message, status.FixHint, status.FixLink));
            }
        }

        return new CapabilityReport(reported.CheckedAt, checks);
    }

    public static Capability? CapabilityNamed(string name) => name switch
    {
        "desktop_session" => Capability.DesktopSession,
        "microphone" => Capability.Microphone,
        "screen_capture" => Capability.ScreenCapture,
        "input_hooks" => Capability.InputHooks,
        "elevated_windows" => Capability.ElevatedWindows,
        _ => null,
    };

    public static CapabilityState StateNamed(string name) => name switch
    {
        "ok" => CapabilityState.Ok,
        "degraded" => CapabilityState.Degraded,
        "blocked" => CapabilityState.Blocked,
        _ => CapabilityState.Unknown,
    };

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
