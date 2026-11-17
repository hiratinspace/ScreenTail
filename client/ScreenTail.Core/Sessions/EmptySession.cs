using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Sessions;

/// <summary>What <see cref="EmptySession.Judge"/> found. Counts and durations only, never a title, a process or a word (INV-10).</summary>
/// <param name="Empty">Nothing worth keeping: the session is not drafted, and History keeps a discarded row for it.</param>
/// <param name="DurationMs">The session's active time.</param>
/// <param name="InScopeMs">How long a window that may be captured (a remote tool or an admin tool) was in front.</param>
/// <param name="Events">How many timeline events the session held.</param>
public sealed record EmptySessionVerdict(bool Empty, long DurationMs, long InScopeMs, int Events);

/// <summary>
/// Whether a session that has just ended holds anything a technician may want (M1, 2026-11-13).
///
/// A session starts the moment a remote tool takes focus (<see cref="Detection.SessionTrigger"/>), so
/// passing through AnyDesk on the way to another window starts one, and it used to end as a draft about
/// nothing: a "Draft ready" toast, a row to check in History, and with a backend a request sent for a
/// note with nothing to draw on. Starting later is not the answer, since a session that begins a second
/// late has missed the first thing the technician did. So the session starts as it did, with the pill
/// showing from the first moment (INV-4), and is judged when it ends.
///
/// Narrow on purpose. A session is empty only when every one of these holds:
/// <list type="bullet">
/// <item>no screenshot was kept, and none was lost to redaction (a lost one was something on screen);</item>
/// <item>no transcript segment was kept, and none was still being transcribed when the session ended;</item>
/// <item>no technician action in a window that may be captured: no click there, no typing, shortcut or
/// Enter, no marked moment;</item>
/// <item>it was never paused (the technician acted on it) or suppressed (a guard had a reason to);</item>
/// <item>it was not recovered after a crash (partial capture says something went wrong, and is kept);</item>
/// <item>a captured window was in front for less than the minimum.</item>
/// </list>
/// A click outside scope does not count: it is kept without its position (INV-6), so it is a time and a
/// button and nothing a note could say. An event this rule does not know counts as content, so a new
/// kind of event is kept until someone decides otherwise.
///
/// The minimum is time in front of a captured window, not the session's length. A session started by a
/// brief focus runs on for the grace (90 s by default) after the tool leaves the front, so its length
/// says nothing about how long the technician was in the tool. A long, quiet session in a remote tool
/// (watching a static screen) is kept: its time may still be billable.
/// </summary>
public static class EmptySession
{
    /// <summary>A captured window in front for less than this, with nothing in the session, is not kept.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(60);

    /// <param name="session">The session as the store holds it, after finalize stopped waiting for frames and narration.</param>
    /// <param name="minimum">The time in a captured window under which a session with nothing in it is not kept. Zero or less keeps every session.</param>
    /// <param name="unheard">Narration segments still owed when finalize stopped waiting: something was said.</param>
    public static EmptySessionVerdict Judge(Session session, TimeSpan minimum, int unheard = 0)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Stable: two events at one millisecond keep the order the store wrote them in.
        var events = session.Events.OrderBy(e => e.TsMs).ToList();
        var end = Math.Max(session.DurationMs ?? 0, events.Count == 0 ? 0 : events[^1].TsMs);

        // Until the first focus change the session is taken to be in a captured window. An automatic
        // start is, by definition; a start by hand somewhere else is counted as if it were, which only
        // ever errs towards keeping the session.
        var inScope = true;
        long since = 0;
        long inScopeMs = 0;
        var content = false;
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent)
            {
                case FocusEvent focus:
                    inScopeMs += inScope ? Math.Max(0, focus.TsMs - since) : 0;
                    inScope = focus.Scope is CaptureScope.RemoteTool or CaptureScope.AdminTool;
                    since = focus.TsMs;
                    break;
                case CaptureStateEvent state:
                    content |= state.State is not (Shared.Schema.CaptureState.Recording or Shared.Schema.CaptureState.Finalizing);
                    break;
                case ClickEvent { X: null, Y: null } when !inScope:
                    // Outside scope: the click's time and button, nothing about where (INV-6).
                    break;
                default:
                    content = true;
                    break;
            }
        }

        inScopeMs += inScope ? Math.Max(0, end - since) : 0;
        var empty = minimum > TimeSpan.Zero
            && !content
            && unheard == 0
            && !session.PartialCapture
            && session.Frames.Count == 0
            && session.FramesPurgedUnredacted == 0
            && session.Transcript.Count == 0
            && inScopeMs < (long)minimum.TotalMilliseconds;
        return new EmptySessionVerdict(empty, session.DurationMs ?? 0, inScopeMs, events.Count);
    }
}
