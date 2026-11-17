using ScreenTrail.Core.Detection;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Core.History;

/// <summary>
/// Why a remote session the service saw was not captured (ST-079's coverage banner, spec v0.7.3 S4). A
/// word from a fixed list, never anything about the window (INV-10); the store refuses any other word.
/// </summary>
public enum MissReason
{
    /// <summary>Settings, Capture, Auto-start is off, so the session waited for Ctrl+Alt+R and none came.</summary>
    AutoStartOff,

    /// <summary>The technician (or the admin's policy) switched this remote tool off in Settings.</summary>
    ToolSwitchedOff,

    /// <summary>New sessions were held while an update was being installed (ST-113).</summary>
    StartHeld,

    /// <summary>Nothing was on screen to say capture was on (the app was closed), so nothing started (INV-4).</summary>
    NoIndicator,

    /// <summary>ScreenTrail was still finishing the session before, and no new one began.</summary>
    Busy,
}

/// <summary>The reasons' words in the store and on the pipe (contract v14), and back.</summary>
public static class MissReasons
{
    private static readonly (MissReason Reason, string Wire)[] Words =
    [
        (MissReason.AutoStartOff, "auto_start_off"),
        (MissReason.ToolSwitchedOff, "tool_switched_off"),
        (MissReason.StartHeld, "start_held"),
        (MissReason.NoIndicator, "no_indicator"),
        (MissReason.Busy, "busy"),
    ];

    public static string Wire(MissReason reason) =>
        Words.First(w => w.Reason == reason).Wire;

    /// <summary>The reason a word names, or null for a word this build does not know.</summary>
    public static MissReason? Parse(string? wire) =>
        Words.Where(w => string.Equals(w.Wire, wire, StringComparison.Ordinal)).Select(w => (MissReason?)w.Reason).FirstOrDefault();
}

/// <summary>A remote session the service saw and did not capture: the tool, when, and why. Nothing else (INV-10).</summary>
/// <param name="Tool">The remote tool's kind, in the sessions' own vocabulary, so the tool filter applies to both.</param>
/// <param name="StartedAt">When the tool first came to the front.</param>
/// <param name="EndedAt">When it last left the front.</param>
public sealed record MissedVisit(RemoteToolKind Tool, DateTimeOffset StartedAt, DateTimeOffset EndedAt, MissReason Reason);

/// <summary>
/// Counts the remote sessions the service saw, so the coverage banner has a true denominator (ST-079,
/// spec v0.7.3 S4). Before this nothing on the device counted a remote session that was not captured,
/// and the banner would always have said 100%.
///
/// <b>One visit, not fifty.</b> A remote session observed is a <i>visit</i>: a remote tool in front, and
/// every return to a remote tool within the grace (the same <see cref="SessionTrigger.Grace"/> a session
/// waits out before it stops) is the same visit. A technician who steps into Outlook to read the ticket
/// and back ten times is one visit, as they would be one session. The visit ends once no remote tool has
/// been in front for the grace; its end time is when the tool last left the front.
///
/// <b>Captured or not.</b> A visit during which a session ran, at any moment, was captured, and nothing is
/// recorded for it here: the session is its record. A visit with no session is a miss, kept with the
/// first reason the service had for not starting one. A tool switched off in Settings is still a remote
/// tool in front: its window is a visit, missed for that reason.
///
/// <b>Brief pass-throughs are left out.</b> A visit with a remote tool in front for less than
/// <see cref="Sessions.EmptySession.Minimum"/> altogether is not recorded, as a captured session that
/// short with nothing in it is discarded rather than drafted and left out of the captured count too
/// (<see cref="ICoverageStore.MarkPassThroughAsync"/>). Passing through AnyDesk on the way to another
/// window was never a remote session worth noting, captured or not, so it is in neither number.
///
/// Platform-neutral, on an injected clock, so the grace and the minimum are tested without waiting.
/// Thread-safe: the scope loop, the auto-stop timer and the session machine's listener all call in.
/// </summary>
public sealed class RemoteVisits(Func<TimeSpan> grace, TimeSpan? minimum = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _minimum = minimum ?? Sessions.EmptySession.Minimum;
    private readonly Lock _gate = new();
    private Visit? _open;

    /// <summary>
    /// What a scope decision says about remote sessions: the remote tool in front, and whether it is one
    /// switched off in Settings (a window of a tool that is off is out of scope and still names its tool).
    /// Null when the window is no remote tool's.
    /// </summary>
    public static (RemoteToolKind Tool, bool SwitchedOff)? Sighting(ScopeDecision? decision) => decision switch
    {
        { Scope: CaptureScope.RemoteTool } => (decision.Tool ?? RemoteToolKind.Other, false),
        { Scope: CaptureScope.OutOfScope, Tool: { } off } => (off, true),
        _ => null,
    };

    /// <summary>Whether a visit is open now, for the tests.</summary>
    public bool Open
    {
        get
        {
            lock (_gate)
            {
                return _open is not null;
            }
        }
    }

    /// <summary>
    /// A look at the window in front: at a foreground change and on the auto-stop timer.
    /// </summary>
    /// <param name="decision">The scope decision for the window in front, or null for none.</param>
    /// <param name="capturing">Whether a session is running now (recording, paused or suppressed).</param>
    /// <param name="refused">Why a start the trigger asked for did not happen at this look, or null.</param>
    /// <returns>The visit this look ended, when it ended one that was missed and long enough to count.</returns>
    public MissedVisit? Observe(ScopeDecision? decision, bool capturing, MissReason? refused = null)
    {
        var sighting = Sighting(decision);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var ended = ExpireLocked(now);
            if (sighting is { } seen)
            {
                _open ??= new Visit(seen.Tool, now);
                _open.InFront(now);
                _open.Note(seen.SwitchedOff ? MissReason.ToolSwitchedOff : refused);
            }
            else
            {
                _open?.Away(now);
            }

            if (capturing)
            {
                _open?.Captured();
            }

            return ended;
        }
    }

    /// <summary>A session began, whoever began it: the visit open now was captured.</summary>
    public void SessionRunning()
    {
        lock (_gate)
        {
            _open?.Captured();
        }
    }

    /// <summary>
    /// The service is stopping: the visit open now ends here, as if the grace had run out, so a visit the
    /// technician was in at shutdown is not lost. Null when there was none, or it was captured or brief.
    /// </summary>
    public MissedVisit? End()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_open is not { } visit)
            {
                return null;
            }

            visit.Away(now);
            _open = null;
            return Missed(visit);
        }
    }

    /// <summary>Ends the open visit when no remote tool has been in front for the grace. Call under the gate.</summary>
    private MissedVisit? ExpireLocked(DateTimeOffset now)
    {
        if (_open is not { InFrontNow: false } visit || now - visit.LeftAt < grace())
        {
            return null;
        }

        _open = null;
        return Missed(visit);
    }

    private MissedVisit? Missed(Visit visit) =>
        visit.WasCaptured || visit.Front < _minimum
            ? null
            : new MissedVisit(visit.Tool, visit.StartedAt, visit.LeftAt, visit.Reason ?? MissReason.Busy);

    private sealed class Visit(RemoteToolKind tool, DateTimeOffset startedAt)
    {
        private DateTimeOffset _since = startedAt;

        public RemoteToolKind Tool { get; } = tool;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset LeftAt { get; private set; } = startedAt;

        public bool InFrontNow { get; private set; }

        /// <summary>How long a remote tool was in front, gaps left out.</summary>
        public TimeSpan Front { get; private set; }

        public bool WasCaptured { get; private set; }

        public MissReason? Reason { get; private set; }

        public void InFront(DateTimeOffset now)
        {
            if (InFrontNow)
            {
                Front += Elapsed(now);
            }

            _since = now;
            InFrontNow = true;
        }

        public void Away(DateTimeOffset now)
        {
            if (!InFrontNow)
            {
                return;
            }

            Front += Elapsed(now);
            _since = now;
            LeftAt = now;
            InFrontNow = false;
        }

        /// <summary>The first reason is kept: it is why the visit began uncaptured.</summary>
        public void Note(MissReason? reason) => Reason ??= reason;

        public void Captured() => WasCaptured = true;

        // A clock stepped back counts as no time rather than negative time.
        private TimeSpan Elapsed(DateTimeOffset now) => now > _since ? now - _since : TimeSpan.Zero;
    }
}
