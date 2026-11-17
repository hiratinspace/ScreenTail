using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Core.Detection;

/// <summary>
/// The remote tool window the technician was most recently in, for the tray's Start capture (ST-071,
/// Spec §5 S1 as built v0.7.2).
///
/// Opening the tray menu moves the foreground to the taskbar or to ScreenTrail's own window, so "the window
/// in front", which Ctrl+Alt+R reads, is never the remote session at the moment the tray is clicked. This
/// remembers the last window the scope policy decided was a remote tool and when it was last seen in front,
/// so the tray can start the session the hotkey would have started in it.
///
/// It is only a pick of attribution: what is photographed is still decided from the window in front, frame
/// by frame (INV-5, INV-6). A window that is no longer a remote tool under the policy in force (a tool
/// switched off, an exclusion added since) is not picked, and nor is one that has closed or that has not
/// been in front for <see cref="Within"/>.
///
/// The clock is injected, so the window is tested without waiting five minutes. In Core, so the Mac suite
/// holds all of it.
/// </summary>
/// <param name="time">The clock; the system's outside tests.</param>
/// <param name="within">How long after it was last in front a window may still be picked. Five minutes by default.</param>
public sealed class RecentRemoteTool(TimeProvider? time = null, TimeSpan? within = null)
{
    /// <summary>How long a remote tool window stays the one the tray starts in, once it has left the front.</summary>
    public static readonly TimeSpan DefaultWithin = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private ForegroundWindowInfo? _window;
    private long _seen;

    /// <summary>How long after it was last in front a window may still be picked.</summary>
    public TimeSpan Within { get; } = within ?? DefaultWithin;

    /// <summary>
    /// A look at the window in front and what the policy decided about it: on every foreground change, and on
    /// the auto-stop timer, so a technician working in one window for an hour keeps it current.
    /// </summary>
    public void Saw(ForegroundWindowInfo window, ScopeDecision decision)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(decision);
        if (window.IsNone)
        {
            return;
        }

        lock (_gate)
        {
            if (decision.Scope == CaptureScope.RemoteTool)
            {
                _window = window;
                _seen = _time.GetTimestamp();
            }
            else if (_window is { } known && known.Handle == window.Handle && known.ProcessId == window.ProcessId)
            {
                // The same window, no longer a remote tool (a tab moved on, the tool switched off): forgotten,
                // rather than started in on the strength of what it was.
                _window = null;
            }
        }
    }

    /// <summary>
    /// The window to start in and the policy's decision about it now, or null when there is none: nothing
    /// remembered, last in front longer ago than <see cref="Within"/>, closed since, or no longer a remote
    /// tool under the policy in force.
    /// </summary>
    /// <param name="policy">The scope policy in force now, so a change since the window was seen applies.</param>
    /// <param name="exists">Whether a window handle still names a window.</param>
    public (ForegroundWindowInfo Window, ScopeDecision Decision)? Pick(ScopePolicy policy, Func<nint, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(exists);
        ForegroundWindowInfo? window;
        long seen;
        lock (_gate)
        {
            window = _window;
            seen = _seen;
        }

        if (window is null || _time.GetElapsedTime(seen) > Within || !exists(window.Handle))
        {
            return null;
        }

        var decision = policy.Decide(window);
        return decision.Scope == CaptureScope.RemoteTool ? (window, decision) : null;
    }
}
