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
    private readonly TimeSpan _grace = grace ?? policy.Registry.Grace;
    private long _lastToolSeen;
    private bool _running;
    private string? _runningToolId;

    public bool SessionRunning => _running;

    /// <summary>The tool that started the running session, for the session's <c>remote_tool</c> field.</summary>
    public string? RunningToolId => _runningToolId;

    /// <summary>Time left before an idle session stops, or zero when nothing is pending.</summary>
    public TimeSpan Remaining => _running && _lastToolSeen != 0
        ? _grace - _time.GetElapsedTime(_lastToolSeen) is { Ticks: > 0 } left ? left : TimeSpan.Zero
        : TimeSpan.Zero;

    /// <summary>Call on every foreground change. Also call on a timer, so the grace can expire while nothing moves.</summary>
    public TriggerDecision Observe(ForegroundWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var decision = policy.Decide(window);
        var isRemoteTool = decision.Scope == Shared.Schema.CaptureScope.RemoteTool;

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

        return Tick();
    }

    /// <summary>
    /// Lets the grace expire without a foreground change. A technician who walks away mid-session leaves the
    /// desktop perfectly still, and a session that only ends on the next click would run until they return.
    /// </summary>
    public TriggerDecision Tick()
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
        _running = running;
        _runningToolId = running ? toolId : null;
        _lastToolSeen = running ? _time.GetTimestamp() : 0;
    }
}
