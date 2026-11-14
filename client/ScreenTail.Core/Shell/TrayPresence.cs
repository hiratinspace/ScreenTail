using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>The five icons Spec §5 S1 lists, and nothing else.</summary>
public enum TrayIcon
{
    /// <summary>The brand glyph, Iris on a light taskbar and Ribbon 400 on a dark one. Nothing is being captured.</summary>
    Idle,

    /// <summary>The glyph with a red dot. INV-3: this is on screen whenever capture is running.</summary>
    Recording,

    /// <summary>Capture is on but paused or suppressed: still a session, still not capturing.</summary>
    Paused,

    /// <summary>The idle glyph with a square badge; the tooltip and menu carry <see cref="TrayPresence.DraftsReady"/>.</summary>
    DraftReady,

    /// <summary>A grey glyph with a cross. The service is not answering, so what is being captured is unknown.</summary>
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
    /// one 16-pixel glyph. Offline is deliberately its own state rather than falling back to idle: a grey
    /// icon says "ask me again", an idle icon says "nothing is being captured", and only one of those is
    /// honest when the service has stopped answering.
    /// </summary>
    public static TrayPresence From(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Connection != ServiceConnection.Connected || snapshot.Capture is not { } capture)
        {
            // The icon is up before the first connection finishes, so starting up says so rather than
            // "not running" every morning. The tooltip and the menu's row use the same words.
            var line = snapshot.Connection == ServiceConnection.Connecting ? Connecting : NotRunning;
            return new TrayPresence(TrayIcon.Offline, $"ScreenTail · {line}", line, 0);
        }

        var drafts = capture.DraftsReady;
        var tool = string.IsNullOrEmpty(capture.RemoteTool) ? null : ToolNames.Display(capture.RemoteTool);
        var elapsed = capture.ElapsedMs is { } ms
            ? ShellStatus.Clock(ms)
            : null;

        var (icon, label) = capture.State switch
        {
            "recording" => (TrayIcon.Recording, "Recording"),
            "paused" => (TrayIcon.Paused, "Paused"),
            "suppressed" => (TrayIcon.Paused, "Paused: sensitive window"),
            "finalizing" => (TrayIcon.Paused, "Finishing up"),
            "draft_ready" => (TrayIcon.DraftReady, "Draft ready"),
            "draft_failed" => (TrayIcon.DraftReady, "Draft failed"),
            CaptureStates.DraftPending => (TrayIcon.Idle, "Not capturing: drafting the note"),
            CaptureStates.Idle => (drafts > 0 ? TrayIcon.DraftReady : TrayIcon.Idle, "Not capturing"),

            // A state this build has never heard of, which means a service newer than this UI. Saying
            // "not capturing" is a guess, and it is the one guess INV-4 cannot afford to get wrong,
            // a newer service adding a recording-like state would show the idle ring (2026-09-19
            // review). Unknown is the honest word, and it is what the Offline glyph already means.
            _ => (TrayIcon.Offline, "Capture state unknown"),
        };

        var detail = (tool, elapsed) switch
        {
            (not null, not null) => $"{label}: {tool} ({elapsed})",
            (not null, null) => $"{label}: {tool}",
            (null, not null) => $"{label} ({elapsed})",
            _ => label,
        };

        // A publish that may have reached its ticket and was never confirmed (contract v10), said after
        // the state and only between sessions: while one is being captured the line is about that and
        // nothing else (INV-4, Spec §7's calm density). The menu's row always has room. The tooltip
        // carries it only when the whole of it fits in what Windows shows, never cut short.
        var resting = capture.State is CaptureStates.Idle or CaptureStates.DraftPending or CaptureStates.DraftReady or CaptureStates.DraftFailed;
        if (!resting)
        {
            return new TrayPresence(icon, $"ScreenTail · {detail}", detail, drafts);
        }

        // Between sessions, how many drafts wait, in words: the badge glyph has no number in it (Spec §5 S1,
        // as built v0.6.10). "Draft ready" already says one. Then the publishes not confirmed. The menu's
        // row says all of it; the tooltip says as much as fits whole, dropping from the end.
        var parts = new List<string> { detail };
        if (DraftsCount(capture.State == CaptureStates.DraftReady && drafts == 1 ? 0 : drafts) is { } waiting)
        {
            parts.Add(waiting);
        }

        if (NotConfirmed(capture.PublishesUnconfirmed) is { } doubt)
        {
            parts.Add(doubt);
        }

        var tooltip = $"ScreenTail · {detail}";
        for (var count = parts.Count; count > 0; count--)
        {
            var candidate = $"ScreenTail · {string.Join(" · ", parts.Take(count))}";
            if (candidate.Length <= TooltipLimit)
            {
                tooltip = candidate;
                break;
            }
        }

        return new TrayPresence(icon, tooltip, string.Join(" · ", parts), drafts);
    }

    /// <summary>The longest tooltip Windows shows whole for a tray icon.</summary>
    public const int TooltipLimit = 63;

    /// <summary>The first row while the UI is still making its first connection to the service.</summary>
    public const string Connecting = "Connecting to the capture service";

    /// <summary>The first row when the service has stopped answering, or never did.</summary>
    public const string NotRunning = "Capture service not running";

    /// <summary>"1 draft ready", "3 drafts ready", or null when there is none.</summary>
    public static string? DraftsCount(int drafts) => drafts switch
    {
        <= 0 => null,
        1 => "1 draft ready",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{drafts} drafts ready"),
    };

    /// <summary>"1 publish not confirmed", "3 publishes not confirmed", or null when there is none.</summary>
    public static string? NotConfirmed(int sessions) => sessions switch
    {
        <= 0 => null,
        1 => "1 publish not confirmed",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{sessions} publishes not confirmed"),
    };
}
