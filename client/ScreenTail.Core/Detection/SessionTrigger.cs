namespace ScreenTail.Core.Detection;

/// <param name="Start">A remote tool took focus and no session is running.</param>
/// <param name="Stop">The grace period elapsed with no remote-tool window in sight.</param>
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
/// piece of work — ending the session there would cut one job into five notes.
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
            if (isRemoteTool)
            {
                _lastToolSeen = _time.GetTimestamp();
                if (!_running)
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
    public TriggerDecision Tick()
    {
        lock (_gate)
        {
            return TickLocked();
        }
    }

    private TriggerDecision TickLocked()
    {
        if (!_running || _lastToolSeen == 0 || _time.GetElapsedTime(_lastToolSeen) < _grace)
        {
            return TriggerDecision.Nothing;
        }

        var toolId = _runningToolId;
        _running = false;
        _runningToolId = null;
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
    /// takes it, and the grace of a session started by hand runs from its start, so one begun away from any
    /// remote tool still ends once the grace is out. The same answer again changes nothing, so the grace and
    /// the tool of a session this trigger started are kept.
    /// </summary>
    /// <returns>Whether the trigger changed its mind.</returns>
    public bool Follow(bool running, string? toolId = null)
    {
        lock (_gate)
        {
            if (running == _running)
            {
                return false;
            }

            SetRunningLocked(running, toolId);
            return true;
        }
    }

    private void SetRunningLocked(bool running, string? toolId)
    {
        _running = running;
        _runningToolId = running ? toolId : null;
        _lastToolSeen = running ? _time.GetTimestamp() : 0;
    }
}
