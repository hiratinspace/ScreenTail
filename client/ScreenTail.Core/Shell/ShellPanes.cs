using System.Globalization;
using ScreenTail.Core.Hud;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>What the shell's content area does with one snapshot from the store.</summary>
public enum PaneAction
{
    /// <summary>Leave the content area as it is.</summary>
    None,

    /// <summary>Review is open and there is no session to show.</summary>
    NothingToReview,

    /// <summary>Ask the service for <see cref="PaneStep.SessionId"/> and show the pane when it arrives.</summary>
    LoadReview,

    /// <summary>Show the Review pane already loaded for <see cref="PaneStep.SessionId"/>, or its loading line.</summary>
    ShowReview,

    /// <summary>Show History, and refresh its list.</summary>
    History,

    /// <summary>Show Settings. <see cref="PaneStep.LoadSettings"/> says whether its panels are read again.</summary>
    Settings,
}

/// <param name="SessionId">The session for <see cref="PaneAction.LoadReview"/> and <see cref="PaneAction.ShowReview"/>.</param>
/// <param name="LoadSettings">True when the view has just become Settings, or the service has just come back while it is open.</param>
/// <param name="KeepEdits">
/// A panel holding a change the technician has not saved is left as it is. True for every read since the
/// one Save bar (Spec, Settings layout): entering the view again keeps pending changes, as the service
/// coming back always did.
/// </param>
/// <param name="ReleaseReview">
/// The session the Review pane was for has gone (it was discarded), so the pane is let go of now, while
/// another view shows, rather than kept for a return that would show a note that no longer exists.
/// </param>
public sealed record PaneStep(PaneAction Action, string? SessionId = null, bool LoadSettings = false, bool KeepEdits = false, bool ReleaseReview = false);

/// <summary>
/// Which pane the shell shows, and when it may ask the service for anything (ST-070).
///
/// Decided here rather than in the view model so it is tested without WPF (ADR-0002). Two rules were
/// wrong while it lived there. Every state change the service sent while Settings was open reloaded all
/// four panels, which overwrote whatever the technician had changed and not yet saved, so Save then stored
/// the old values and said "Saved and applied." (2026-10-12 review, S-144, T-161). The panels are read
/// when the view becomes Settings, and again when the service comes back while it is open, then only
/// those with nothing unsaved: Settings opened while the service was away would otherwise say it did not
/// answer until the technician left and came back. And going back to Review for the session it already had left the
/// previous view's pane on screen, because only a different session counted as a change (T-159). Entering
/// Review now always shows it.
///
/// One instance per shell window, used on that window's thread.
/// </summary>
public sealed class ShellPanes
{
    private ShellView? _view;
    private string? _review;
    private bool _reviewHadDraft;
    private bool _reviewFailed;
    private ServiceConnection? _connection;

    /// <summary>The view the content area belongs to now, or null before the first snapshot.</summary>
    public ShellView? View => _view;

    /// <summary>The session the Review pane is for, loaded or loading. Null when there is none.</summary>
    public string? Review => _review;

    public PaneStep Next(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entering = _view != snapshot.View;
        _view = snapshot.View;

        // Back after being away, or answering for the first time after connecting. The store also counts
        // an event that arrives while it thought the service was away as the service back.
        var reconnected = _connection is not null and not ServiceConnection.Connected
            && snapshot.Connection == ServiceConnection.Connected;
        _connection = snapshot.Connection;

        // The store names a session to review from the moment one is chosen and never names none again,
        // except when that session is discarded. Away from Review then, the pane for it is let go of.
        var release = snapshot.View != ShellView.Review && snapshot.Reviewing is null && _review is not null;
        if (release)
        {
            _review = null;
            _reviewHadDraft = false;
            _reviewFailed = false;
        }

        switch (snapshot.View)
        {
            case ShellView.Review when snapshot.Reviewing is null:
                _review = null;
                _reviewHadDraft = false;
                _reviewFailed = false;
                return new PaneStep(PaneAction.NothingToReview);

            case ShellView.Review when snapshot.Reviewing != _review:
                _review = snapshot.Reviewing;
                _reviewHadDraft = Drafted(snapshot, _review);
                _reviewFailed = false;
                return new PaneStep(PaneAction.LoadReview, _review);

            case ShellView.Review when entering && _reviewFailed:
                // The service would not hand it over last time, and the technician has come back for it.
                // Asked again then, and not on every state change in between.
                _reviewFailed = false;
                return new PaneStep(PaneAction.LoadReview, _review);

            case ShellView.Review when !_reviewHadDraft && Drafted(snapshot, _review!):
                // Opened while it was still being recorded (the pill's double-click), and the draft has
                // just arrived: the pane on screen is the session without its note.
                _reviewHadDraft = true;
                _reviewFailed = false;
                return new PaneStep(PaneAction.LoadReview, _review);

            case ShellView.Review when entering:
                return new PaneStep(PaneAction.ShowReview, _review);

            case ShellView.Review:
                return new PaneStep(PaneAction.None);

            case ShellView.History:
                return new PaneStep(PaneAction.History, ReleaseReview: release);

            case ShellView.Settings:
                // Spec, Settings layout (owner decision): leaving Settings keeps the changes pending, so
                // coming back reads only the panels with nothing pending, the same as a reconnect does.
                var load = entering || reconnected;
                return new PaneStep(PaneAction.Settings, LoadSettings: load, KeepEdits: load, ReleaseReview: release);

            default:
                return new PaneStep(PaneAction.None);
        }
    }

    /// <summary>
    /// The service would not hand the session over. It is asked again when the technician comes back to
    /// Review for it, and not on every state change before then.
    /// </summary>
    public void Failed(string sessionId)
    {
        if (_review == sessionId)
        {
            _reviewFailed = true;
        }
    }

    private static bool Drafted(ShellSnapshot snapshot, string sessionId) =>
        snapshot.Capture is { } capture
        && capture.SessionId == sessionId
        && capture.State is CaptureStates.DraftReady or CaptureStates.DraftFailed;
}

/// <summary>The shell's status line (Spec §5 S1's tooltip wording), from the store's snapshot.</summary>
public static class ShellStatus
{
    public const string Connecting = "Connecting to the capture service…";

    public const string NotConnected = "Not connected to the capture service";

    public const string Unknown = "Capture state unknown";

    /// <summary>Review with nothing to show between sessions: how to make a draft.</summary>
    public const string NothingToReviewYet = "No draft to review yet. Record a session and stop it with Ctrl+Alt+S.";

    /// <summary>Review with nothing to show while a session is being captured: the draft is on its way here.</summary>
    public const string CapturingNow = "Capturing now: the draft opens here when you stop (Ctrl+Alt+S).";

    /// <summary>
    /// What Review says with no session to show. While one is being captured it says the draft is coming
    /// and where, rather than telling the technician to record what they are recording. Only what capture
    /// is doing now counts: with nobody answering, nothing is claimed (INV-4).
    /// </summary>
    public static string NothingToReview(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.KnownCapture?.State is CaptureStates.Recording or CaptureStates.Paused or CaptureStates.Suppressed
            ? CapturingNow
            : NothingToReviewYet;
    }

    /// <summary>
    /// The status chip's tone: the colour of its dot and border, as the pill would show it (Spec §5 S2).
    /// Only what capture is doing now counts, so a lost service is never drawn as recording.
    /// </summary>
    public static HudTone Tone(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Connection != ServiceConnection.Connected)
        {
            return HudTone.Idle;
        }

        return snapshot.KnownCapture?.State switch
        {
            CaptureStates.Recording => HudTone.Recording,
            CaptureStates.Paused or CaptureStates.Suppressed or CaptureStates.Finalizing => HudTone.Paused,
            _ => HudTone.Idle,
        };
    }

    /// <summary>
    /// The tray's and the status line's time: m:ss, or h:mm:ss past the hour. It was the m:ss of a
    /// TimeSpan, whose minutes start again at each hour, so an hour and five minutes read "5:00" once the
    /// clock could run that long on screen (ST-072).
    /// </summary>
    public static string Clock(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }

    /// <summary>
    /// <see cref="Describe"/> without the time: what the status line is announced on (ST-084). The time
    /// moves every second of a recording (ST-072), and a screen reader interrupted that often hears nothing.
    /// </summary>
    public static string Announcement(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Capture is { ElapsedMs: not null } capture
            ? Describe(snapshot with { Capture = capture with { ElapsedMs = null } })
            : Describe(snapshot);
    }

    /// <summary>
    /// The state, the tool, and how long. A session that has been running for twelve minutes says so,
    /// because "recording" alone does not tell a technician whether the thing they started an hour ago is
    /// still going.
    ///
    /// A state this build does not know is called unknown, as the tray and the pill call it. The shell used
    /// to call it "Idle", which is the one guess INV-4 cannot afford: a newer service's recording-like state
    /// would read as nothing happening in one window while the other two said otherwise (T-162).
    /// </summary>
    public static string Describe(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Connection == ServiceConnection.Connecting)
        {
            return Connecting;
        }

        if (snapshot.KnownCapture is not { } capture)
        {
            return NotConnected;
        }

        var state = capture.State switch
        {
            CaptureStates.Recording => "Recording",
            CaptureStates.Paused => "Paused",
            CaptureStates.Suppressed => "Paused: sensitive window",
            CaptureStates.Finalizing => "Finishing up",
            CaptureStates.DraftReady => "Draft ready",
            CaptureStates.DraftFailed => "Draft failed",
            CaptureStates.DraftPending => "Drafting the note",
            CaptureStates.Idle => "Idle",
            _ => Unknown,
        };

        var tool = string.IsNullOrEmpty(capture.RemoteTool) ? null : ToolNames.Display(capture.RemoteTool);
        var elapsed = capture.ElapsedMs is { } ms
            ? Clock(ms)
            : null;

        return (tool, elapsed) switch
        {
            (not null, not null) => $"{state}: {tool} ({elapsed})",
            (not null, null) => $"{state}: {tool}",
            (null, not null) => $"{state} ({elapsed})",
            _ => state,
        };
    }
}
