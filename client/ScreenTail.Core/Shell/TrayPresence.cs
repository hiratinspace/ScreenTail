using System.Globalization;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>The five icons Spec §5 S1 lists, and nothing else.</summary>
public enum TrayIcon
{
    /// <summary>A tail glyph in text.secondary. Nothing is being captured.</summary>
    Idle,

    /// <summary>state.recording with a dot. INV-3: this is on screen whenever capture is running.</summary>
    Recording,

    /// <summary>Capture is on but paused or suppressed — still a session, still not capturing.</summary>
    Paused,

    /// <summary>accent.primary with a badge carrying <see cref="TrayPresence.DraftsReady"/>.</summary>
    DraftReady,

    /// <summary>Grey. The service is not answering, so what is being captured is unknown.</summary>
    Offline,
}

/// <param name="Tooltip">Spec §5 S1's wording. A process name at most, never a window title (INV-10).</param>
/// <param name="StateLine">The menu's first row, which is not interactive.</param>
public sealed record TrayPresence(TrayIcon Icon, string Tooltip, string StateLine, int DraftsReady)
{
    /// <summary>
    /// What the tray shows, derived from the last thing the service said (ST-071).
    ///
    /// INV-3 and INV-4 both land here: the icon is the one indicator that is always on screen, so
    /// "recording" has to be distinguishable from "paused" and from "we do not know" at a glance and in
    /// one 16-pixel glyph. Offline is deliberately its own state rather than falling back to idle — a grey
    /// icon says "ask me again", an idle icon says "nothing is being captured", and only one of those is
    /// honest when the service has stopped answering.
    /// </summary>
    public static TrayPresence From(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Connection != ServiceConnection.Connected || snapshot.Capture is not { } capture)
        {
            return new TrayPresence(
                TrayIcon.Offline,
                "ScreenTail — not connected to the capture service",
                "Capture service not running",
                0);
        }

        var drafts = capture.DraftsReady;
        var tool = string.IsNullOrEmpty(capture.RemoteTool) ? null : capture.RemoteTool;
        var elapsed = capture.ElapsedMs is { } ms
            ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", CultureInfo.InvariantCulture)
            : null;

        var (icon, label) = capture.State switch
        {
            "recording" => (TrayIcon.Recording, "Recording"),
            "paused" => (TrayIcon.Paused, "Paused"),
            "suppressed" => (TrayIcon.Paused, "Paused — sensitive window"),
            "finalizing" => (TrayIcon.Paused, "Finishing up"),
            "draft_ready" => (TrayIcon.DraftReady, "Draft ready"),
            "draft_failed" => (TrayIcon.DraftReady, "Draft failed"),
            _ => (drafts > 0 ? TrayIcon.DraftReady : TrayIcon.Idle, "Not capturing"),
        };

        var detail = (tool, elapsed) switch
        {
            (not null, not null) => $"{label} — {tool} ({elapsed})",
            (not null, null) => $"{label} — {tool}",
            (null, not null) => $"{label} ({elapsed})",
            _ => label,
        };

        return new TrayPresence(icon, $"ScreenTail — {detail}", detail, drafts);
    }
}
