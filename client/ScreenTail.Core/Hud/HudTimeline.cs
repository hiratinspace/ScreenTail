using System.Globalization;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Hud;

/// <summary>The kinds of event the expanded pill lists. A kind and a count, never content (INV-10).</summary>
public enum HudEventKind
{
    /// <summary>The redaction backlog grew: a screenshot was taken and is being masked.</summary>
    ScreenshotTaken,

    /// <summary>The redaction backlog shrank: a screenshot finished masking.</summary>
    ScreenshotMasked,

    /// <summary>The pill's Mark moment went through.</summary>
    MomentMarked,

    /// <summary>The technician paused.</summary>
    Paused,

    /// <summary>Capture is running again after a pause or a guard.</summary>
    Resumed,

    /// <summary>A guard paused capture: a sensitive field, an excluded app, an elevated window.</summary>
    PausedAutomatically,

    /// <summary>The window in front is out of scope: clicks only, no screenshots.</summary>
    NotCapturing,
}

/// <param name="Kind">What happened.</param>
/// <param name="Text">The kind in words, with its count ("2 screenshots masked").</param>
/// <param name="Time">When the UI heard of it, <c>HH:mm:ss</c> local time.</param>
public sealed record HudEvent(HudEventKind Kind, string Text, string Time)
{
    /// <summary>The row as a screen reader says it.</summary>
    public string Spoken => $"{Text}, {Time}";
}

/// <summary>
/// The expanded pill's session card: the tool and start, and the last three events (Spec §5 S2, v0.6.0).
///
/// Built only from what the UI already hears: each capture state snapshot, compared with the one before,
/// and the marks the pill itself sent and the service accepted. The redaction backlog rising is a
/// screenshot taken, falling is one masked; the state moving is a pause, a resume or a guard. The service
/// is asked for nothing more, and no event is invented from silence (ADR-0003): a dropped pipe changes
/// nothing here.
///
/// INV-10: the pill sits over a customer's desktop, so every event is a kind, a count and a time. The
/// snapshot's process name, draft title and company never reach these words, and nor does any window
/// title, screen text or transcript, because none of those cross the pipe to here in the first place.
/// </summary>
public sealed class HudTimeline
{
    /// <summary>How many events the expanded pill shows.</summary>
    public const int Shown = 3;

    private readonly TimeProvider _time;
    private readonly List<HudEvent> _recent = [];
    private CaptureStateSnapshot? _last;
    private string? _session;

    /// <param name="time">The wall clock events are stamped with, and its time zone.</param>
    public HudTimeline(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>"ScreenConnect session, since 14:02"; null between sessions.</summary>
    public string? SessionTitle { get; private set; }

    /// <summary>The last <see cref="Shown"/> events, newest first.</summary>
    public IReadOnlyList<HudEvent> Recent => [.. _recent];

    /// <summary>Takes the service's latest state.</summary>
    /// <param name="capture">The last thing the service said; null while it is not heard from.</param>
    /// <returns>Whether the title or the events changed.</returns>
    public bool Observe(CaptureStateSnapshot? capture)
    {
        if (capture is null)
        {
            // Not heard from is not an event, and not a reason to forget what the pill knew.
            return false;
        }

        var session = HudState.SessionUnderWay(capture);
        if (session is null)
        {
            var had = SessionTitle is not null || _recent.Count > 0;
            SessionTitle = null;
            _recent.Clear();
            _session = null;
            _last = capture;
            return had;
        }

        if (!string.Equals(session, _session, StringComparison.Ordinal))
        {
            _session = session;
            _recent.Clear();
            var since = Now().AddMilliseconds(-Math.Max(0, capture.ElapsedMs ?? 0));
            var tool = string.IsNullOrWhiteSpace(capture.RemoteTool) ? "Remote" : ToolNames.Display(capture.RemoteTool);
            SessionTitle = string.Create(CultureInfo.InvariantCulture, $"{tool} session, since {since:HH:mm}");
            _last = capture;
            return true;
        }

        var previous = _last;
        _last = capture;
        var changed = false;

        var grew = capture.PendingRedactions - (previous?.PendingRedactions ?? 0);
        if (grew > 0)
        {
            changed |= Add(HudEventKind.ScreenshotTaken, grew == 1 ? "Screenshot taken" : Count(grew, "screenshots taken"));
        }
        else if (grew < 0)
        {
            changed |= Add(HudEventKind.ScreenshotMasked, -grew == 1 ? "1 screenshot masked" : Count(-grew, "screenshots masked"));
        }

        if (previous is not null && (previous.State != capture.State || previous.Reason != capture.Reason))
        {
            changed |= capture.State switch
            {
                CaptureStates.Recording => Add(HudEventKind.Resumed, "Resumed"),
                CaptureStates.Paused when capture.Reason is CaptureReasons.User or null => Add(HudEventKind.Paused, "Paused"),
                CaptureStates.Suppressed or CaptureStates.Paused when capture.Reason is CaptureReasons.OutOfScope => Add(HudEventKind.NotCapturing, "Not capturing"),
                CaptureStates.Suppressed or CaptureStates.Paused => Add(HudEventKind.PausedAutomatically, "Paused automatically"),
                _ => false,
            };
        }

        return changed;
    }

    /// <summary>The service accepted a Mark moment the pill sent. Ignored between sessions.</summary>
    /// <returns>Whether it was recorded.</returns>
    public bool MomentMarked() => _session is not null && Add(HudEventKind.MomentMarked, "Moment marked");

    private bool Add(HudEventKind kind, string text)
    {
        _recent.Insert(0, new HudEvent(kind, text, Now().ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        if (_recent.Count > Shown)
        {
            _recent.RemoveRange(Shown, _recent.Count - Shown);
        }

        return true;
    }

    private DateTimeOffset Now() => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);

    private static string Count(int n, string what) => string.Create(CultureInfo.InvariantCulture, $"{n} {what}");
}
