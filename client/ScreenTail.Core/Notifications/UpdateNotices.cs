using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Notifications;

/// <summary>
/// The one thing ScreenTail says about an update, and when (ST-113 AC1: prompt only when idle).
///
/// Spec §4's rule holds here as everywhere: nothing is said while a session is running. Afterwards, an
/// update that is downloaded, verified and ready is announced once per version, in the tray, with
/// where to go to install it. Nothing is said about checking, downloading or a refusal: those are in
/// Settings for whoever looks, and a toast about them is noise.
/// </summary>
public static class UpdateNotices
{
    /// <returns>What to say, or null to say nothing.</returns>
    public static Notification? For(CaptureStateSnapshot? capture, UpdateReported? update)
    {
        if (capture is null || update is null)
        {
            return null;
        }

        if (capture.State is CaptureStates.Recording or CaptureStates.Paused or CaptureStates.Suppressed or CaptureStates.Finalizing)
        {
            return null;
        }

        return update.State == "ready" && update.AvailableVersion is { Length: > 0 } version
            ? new Notification(
                $"Update ready: ScreenTail {version}. Open Settings, then Updates, to install it",
                NotificationAction.None,
                $"update-ready:{version}")
            : null;
    }
}
