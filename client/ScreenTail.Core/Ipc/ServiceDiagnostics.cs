using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// What the diagnostics panel shows, assembled from the things only the service can see (ST-071, ST-085).
///
/// Every field is a state, a count or a device name. The active window appears as the scope decision's
/// own words, a process name and what is being done about it, because this text goes on a clipboard and
/// into tickets, and a window title there is a customer's business in someone's ticket (INV-10).
/// </summary>
public static class ServiceDiagnostics
{
    /// <summary>The scope line when no scope decision has been made yet.</summary>
    public const string NothingInFront = "Not capturing — no remote session in front";

    public static DiagnosticsReported Report(
        string serviceVersion,
        string? scope,
        string? microphone,
        CaptureStateSnapshot state,
        Func<CaptureStateReason, bool> isHeld,
        int redactionBacklog,
        RedactionProgress? redaction,
        long? keystrokesDropped,
        long egressBlocked,
        bool localOnly,
        string policyVersion,
        long workingSetBytes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(isHeld);
        return new DiagnosticsReported
        {
            Scope = scope ?? NothingInFront,
            Microphone = microphone,
            Suppression = null,
            RedactionBacklog = 0,
            FramesDropped = (redaction?.Unread ?? 0) + (redaction?.Unreadable ?? 0),
            KeystrokesDropped = keystrokesDropped ?? 0,
            EgressBlocked = egressBlocked,
            LocalOnly = localOnly,
            PolicyVersion = policyVersion,
            CpuPercent = 0,
            WorkingSetBytes = workingSetBytes,
            ServiceVersion = serviceVersion,
        };
    }

    public static string? Suppression(CaptureStateSnapshot state, Func<CaptureStateReason, bool> isHeld)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(isHeld);
        return null;
    }
}
