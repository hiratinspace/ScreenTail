namespace ScreenTail.Core.Detection;

/// <param name="Start">A remote tool took focus and no session is running.</param>
/// <param name="Stop">The grace period elapsed since the session's last remote tool (or own window) left the front.</param>
public sealed record TriggerDecision(bool Start, bool Stop, string? ToolId, string Reason)
{
    public static readonly TriggerDecision Nothing = new(false, false, null, "no change");
}

/// <summary>
/// Decides when a session starts and stops on its own (ST-023).
///
/// Starting is immediate: a technician who opens ScreenConnect is working, and a session that begins a
/// second late has already missed the first thing they did. Stopping waits out a grace period, because
/// stepping into Outlook to read the ticket, or into PowerShell to check something, is part of the same
/// piece of work, and ending the session there would cut one job into five notes. The grace runs from when
/// the tool loses focus: while it is in front, every look at the foreground starts it over, so a technician
/// who works in one window for an hour makes no foreground change and is not stopped for it.
///
/// The clock is injected, so the grace is tested in microseconds rather than by waiting ninety seconds.
/// </summary>
public sealed class SessionTrigger(ScopePolicy policy, TimeProvider? time = null, TimeSpan? grace = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Asked from the scope loop, the auto-stop timer and whichever thread moved the session machine, so
    // each question and its answer are one step. Never held across anything that waits.
    private readonly Lock _gate = new();
    private TimeSpan _grace = grace ?? policy.Registry.Grace;
    private long _lastToolSeen;
    private bool _running;
    private string? _runningToolId;

    // The window a session started by hand was started in. It keeps the session as a remote tool does,
    // so Ctrl+Alt+R over a console no registry knows is not ended while the technician is still in it.
    // Known by its handle and its process together: Windows hands a closed window's handle out again, and
    // another application's window under the same handle is not the one the session was started in.
    private nint _ownWindow;
    private int _ownProcess;

    /// <summary>The grace in force. The technician's setting changes it in place (ST-080).</summary>
    public TimeSpan Grace
    {
        get
        {
            lock (_gate)
            {
                return _grace;
            }
        }
    }

    public void Apply(TimeSpan grace)
    {
        lock (_gate)
        {
            _grace = grace;
        }
    }

    public bool SessionRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The tool that started the running session, for the session's <c>remote_tool</c> field.</summary>
    public string? RunningToolId
    {
        get
        {
            lock (_gate)
            {
                return _runningToolId;
            }
        }
    }

    /// <summary>Time left before an idle session stops, or zero when nothing is pending.</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                return _running && _lastToolSeen != 0
                    ? _grace - _time.GetElapsedTime(_lastToolSeen) is { Ticks: > 0 } left ? left : TimeSpan.Zero
                    : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Call on every foreground change. Also call on a timer, so the grace can expire while nothing moves.</summary>
    public TriggerDecision Observe(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var decision = policy.Decide(window);
        var isRemoteTool = decision.Scope == Shared.Schema.CaptureScope.RemoteTool;

        lock (_gate)
        {
            if (isRemoteTool || IsOwnLocked(window))
            {
                _lastToolSeen = _time.GetTimestamp();
                if (!_running && isRemoteTool)
                {
                    _running = true;
                    _runningToolId = decision.ToolId;
                    return new TriggerDecision(true, false, decision.ToolId, decision.Reason);
                }
            }

            return TickLocked();
        }
    }

    /// <summary>
    /// Lets the grace expire without a foreground change. A technician who walks away mid-session leaves the
    /// desktop perfectly still, and a session that only ends on the next click would run until they return.
    /// </summary>
    public TriggerDecision Tick() => Tick(inFront: null);

    /// <summary>
    /// As <see cref="Tick()"/>, told what is in front now. A remote tool, or the window a session started by
    /// hand was started in, keeps the session: the grace starts over from this moment, so it only runs out
    /// once that window has been out of the front for all of it. Never starts a session; only a foreground
    /// change does that.
    /// </summary>
    public TriggerDecision Tick(ForegroundWindowInfo? inFront)
    {
        var holds = inFront is not null && policy.Decide(inFront).Scope == Shared.Schema.CaptureScope.RemoteTool;
        lock (_gate)
        {
            if (_running && inFront is not null && (holds || IsOwnLocked(inFront)))
            {
                _lastToolSeen = _time.GetTimestamp();
            }

            return TickLocked();
        }
    }

    /// <summary>Whether this is the window the running session was started in by hand. Call under the gate.</summary>
    private bool IsOwnLocked(ForegroundWindowInfo window) =>
        _running && _ownWindow != 0 && window.Handle == _ownWindow && window.ProcessId == _ownProcess;

    private TriggerDecision TickLocked()
    {
        if (!_running || _lastToolSeen == 0 || _time.GetElapsedTime(_lastToolSeen) < _grace)
        {
            return TriggerDecision.Nothing;
        }

        var toolId = _runningToolId;
        _running = false;
        _runningToolId = null;
        _ownWindow = 0;
        _ownProcess = 0;
        _lastToolSeen = 0;
        return new TriggerDecision(false, true, toolId, $"No remote session for {_grace.TotalSeconds:F0} seconds.");
    }

    /// <summary>The technician started or stopped the session themselves; the trigger follows rather than argues.</summary>
    public void SetRunning(bool running, string? toolId = null)
    {
        lock (_gate)
        {
            SetRunningLocked(running, toolId);
        }
    }

    /// <summary>
    /// The session machine is, or is not, running a session now, whoever started or stopped it: the chord, the
    /// tray, a refused start, a stop that failed. A change of answer is taken as <see cref="SetRunning"/>
    /// takes it. A session started by hand is kept while <paramref name="window"/>, the window it was started
    /// in, or a remote tool is in front, and once neither is the grace runs out as it does for any other,
    /// so the microphone of one begun away from any remote tool is not left open for good. The same answer
    /// again changes nothing, so the grace and the tool of a session this trigger started are kept.
    /// </summary>
    /// <returns>Whether the trigger changed its mind.</returns>
    public bool Follow(bool running, string? toolId = null, ForegroundWindowInfo? window = null)
    {
        lock (_gate)
        {
            if (running == _running)
            {
                return false;
            }

            SetRunningLocked(running, toolId);
            _ownWindow = running && window is not null ? window.Handle : 0;
            _ownProcess = running && window is not null ? window.ProcessId : 0;
            return true;
        }
    }

    private void SetRunningLocked(bool running, string? toolId)
    {
        _running = running;
        _runningToolId = running ? toolId : null;
        _ownWindow = 0;
        _ownProcess = 0;
        _lastToolSeen = running ? _time.GetTimestamp() : 0;
    }
}
