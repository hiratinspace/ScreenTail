using System.Globalization;
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
/// The read is for the service coming back, not for entering the view: a panel holding a change the
/// technician has not saved is left as it is.
/// </param>
public sealed record PaneStep(PaneAction Action, string? SessionId = null, bool LoadSettings = false, bool KeepEdits = false);

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
                return new PaneStep(PaneAction.History);

            case ShellView.Settings:
                return new PaneStep(PaneAction.Settings, LoadSettings: entering || reconnected, KeepEdits: !entering && reconnected);

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
            CaptureStates.Idle => "Idle",
            _ => Unknown,
        };

        var tool = string.IsNullOrEmpty(capture.RemoteTool) ? null : capture.RemoteTool;
        var elapsed = capture.ElapsedMs is { } ms
            ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", CultureInfo.InvariantCulture)
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
