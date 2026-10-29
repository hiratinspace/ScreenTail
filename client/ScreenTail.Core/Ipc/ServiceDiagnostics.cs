using System.Text.Json;
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
///
/// The host used to build this itself and hard-coded two fields: suppression was always null and the
/// redaction backlog always 0, so the panel said "suppressed: no" over a password field and "queue 0"
/// with frames waiting (2026-10-12 review, S-129 and T-113). In Core, so the Mac suite holds every field
/// to its source.
/// </summary>
public static class ServiceDiagnostics
{
    /// <summary>The scope line when no scope decision has been made yet.</summary>
    public const string NothingInFront = "Not capturing: no remote session in front";

    /// <summary>Every reason with the name the state events and the contract give it, worked out once.</summary>
    private static readonly (CaptureStateReason Reason, string Name)[] Reasons =
        [.. Enum.GetValues<CaptureStateReason>().Select(reason => (reason, JsonSerializer.Serialize(reason, SessionJson.Options).Trim('"')))];

    /// <param name="state">The machine's snapshot, for whether it is suppressed.</param>
    /// <param name="isHeld">Whether a reason is holding the session down, asked of the machine.</param>
    /// <param name="redactionBacklog">Frames waiting to be read, as the redaction worker last reported.</param>
    /// <param name="framesRefused">
    /// Frames the queue of frames waiting to be read refused for want of room, for the life of the
    /// service. Lost to INV-1 like a frame the worker could not read, and counted in the same field: the
    /// panel said 0 frames dropped while the queue was turning frames away.
    /// </param>
    public static DiagnosticsReported Report(
        string serviceVersion,
        string? scope,
        string? microphone,
        CaptureStateSnapshot state,
        Func<CaptureStateReason, bool> isHeld,
        int redactionBacklog,
        RedactionProgress? redaction,
        long framesRefused,
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
            Microphone = microphone, // A device name, never audio (INV-10).
            Suppression = Suppression(state, isHeld),
            RedactionBacklog = Math.Max(0, redactionBacklog),
            FramesDropped = (redaction?.Unread ?? 0) + (redaction?.Unreadable ?? 0) + framesRefused,
            KeystrokesDropped = keystrokesDropped ?? 0,
            EgressBlocked = egressBlocked,
            LocalOnly = localOnly,
            PolicyVersion = policyVersion,
            CpuPercent = 0,
            WorkingSetBytes = workingSetBytes,
            ServiceVersion = serviceVersion,
        };
    }

    /// <summary>
    /// What is holding capture down, in the contract's names, or null when nothing is.
    ///
    /// The state decides whether it is a suppression, not the holds: a pause keeps its holds (T-41), and
    /// the HUD says "paused" then, so the panel must too. A suppressed state whose hold cannot be named
    /// still says suppressed, rather than "no".
    /// </summary>
    public static string? Suppression(CaptureStateSnapshot state, Func<CaptureStateReason, bool> isHeld)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(isHeld);
        if (state.State != CaptureStates.Suppressed)
        {
            return null;
        }

        var held = Reasons.Where(r => r.Reason != CaptureStateReason.User && isHeld(r.Reason)).Select(r => r.Name).ToList();
        return held.Count == 0 ? CaptureStates.Suppressed : string.Join(", ", held);
    }
}
