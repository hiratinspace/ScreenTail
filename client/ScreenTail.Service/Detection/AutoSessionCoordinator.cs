using System.Runtime.Versioning;
using System.Threading.Channels;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Detection;

/// <summary>
/// Joins the foreground watcher to the session machine (ST-023): a remote tool takes focus and a session
/// starts; the technician stays away from one for the grace period and it stops.
///
/// The watcher raises its event on the thread pumping the WinEvent hook, and that thread must stay free —
/// it is the same thread ST-022 measures at 25 ms for a focus change. So the handler does nothing but drop
/// the window into a channel, and all the work (a database write, starting a session) happens here.
///
/// The channel is bounded and drops the oldest when full: falling behind on window changes should cost the
/// oldest observation, not the watcher's responsiveness.
/// </summary>
[SupportedOSPlatform("windows")]
/// <param name="indicated">
/// Whether anything is on screen saying capture is happening. A session that starts with no indicator is
/// a recording nobody was told about (INV-4), and one that should never have begun is worse than one
/// suppressed a moment later: it leaves a start time in the timeline that nobody witnessed.
/// </param>
internal sealed partial class AutoSessionCoordinator(
    SessionMachine machine,
    ScopePolicy policy,
    SessionTrigger trigger,
    Func<bool> indicated,
    ILogger logger)
{
    private readonly Channel<ForegroundWindowInfo> _windows = Channel.CreateBounded<ForegroundWindowInfo>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private ScopeDecision? _lastReported;

    /// <summary>What the HUD shows about scope right now (Spec §5 S2).</summary>
    public ScopeDecision? CurrentScope => _lastReported;

    /// <summary>Called from the watcher's pump thread. Must not block, and does not.</summary>
    public void Observe(ForegroundWindowInfo window) => _windows.Writer.TryWrite(window);

    /// <summary>
    /// Starts a session for whatever is in front, for the Ctrl+Alt+R chord and the tray's "Start capture"
    /// (ST-029).
    ///
    /// The coordinator is asked rather than the machine directly, because starting needs a remote tool to
    /// attribute the session to and only this knows what the technician is looking at. A window it does not
    /// recognise still starts a session, as <c>other</c>: a technician reaching for the chord has decided
    /// this is support work, and refusing because the tool is not in the registry would make the manual
    /// path useless exactly when the automatic one has already failed.
    /// </summary>
    public async Task<bool> StartFromForegroundAsync(CancellationToken ct = default)
    {
        if (machine.State is not (SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed))
        {
            return false;
        }

        if (!indicated())
        {
            LogNoIndicator(logger);
            return false;
        }

        var tool = new RemoteTool { Kind = _lastReported?.Tool ?? RemoteToolKind.Other };
        var started = await machine.StartAsync(tool, ct: ct).ConfigureAwait(false);
        if (started)
        {
            LogManualStart(logger, _lastReported?.ToolId ?? "unknown");
        }

        return started;
    }

    /// <summary>Avoids Enum.ToString() on a path the log may not even be listening to.</summary>
    private static string ScopeName(CaptureScope scope) => scope switch
    {
        CaptureScope.RemoteTool => "remote_tool",
        CaptureScope.AdminTool => "admin_tool",
        CaptureScope.Excluded => "excluded",
        _ => "out_of_scope",
    };

    public async Task RunAsync(CancellationToken ct)
    {
        // The grace has to expire on a timer: a technician who walks away produces no window changes at all.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var ticking = TickAsync(timer, ct);

        // One window that cannot be handled costs that window. If this loop ends, the last scope decision
        // stays in force for every window that follows — and if that decision was "remote tool", typing
        // anywhere is recorded from then on. It used to end on the first exception of any kind.
        var windows = _windows.Reader;
        await ResilientLoop.RunAsync(
            next: async token => await windows.WaitToReadAsync(token).ConfigureAwait(false),
            step: async token =>
            {
                while (windows.TryRead(out var window))
                {
                    await HandleAsync(window, token).ConfigureAwait(false);
                }
            },
            onFailure: failure => LogLoopFailed(logger, "scope", failure.GetType().Name),
            ct).ConfigureAwait(false);

        await ticking.ConfigureAwait(false);
    }

    /// <summary>
    /// One foreground change. Internal rather than private so the scope decision it leaves behind can be
    /// asserted directly; the class is already internal to this assembly and its tests.
    /// </summary>
    internal async Task HandleAsync(ForegroundWindowInfo window, CancellationToken ct)
    {
        var decision = policy.Decide(window);
        var triggered = trigger.Observe(window);

        if (triggered.Start && machine.State is SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed)
        {
            var tool = new RemoteTool { Kind = decision.Tool ?? RemoteToolKind.Other };
            if (!indicated())
            {
                LogNoIndicator(logger);
            }
            else if (await machine.StartAsync(tool, ct: ct).ConfigureAwait(false))
            {
                LogAutoStart(logger, triggered.ToolId ?? "unknown");
            }
        }
        else if (triggered.Stop && machine.State is SessionState.Recording or SessionState.Paused or SessionState.Suppressed)
        {
            LogAutoStop(logger, triggered.Reason);
            await machine.StopAsync(ct).ConfigureAwait(false);
        }

        // The timeline records where the technician went and whether it was in scope, even when no frame is
        // taken — that is what makes "clicks logged, no frames" visible in Review rather than a silent gap.
        if (_lastReported?.Scope != decision.Scope || _lastReported?.ToolId != decision.ToolId)
        {
            _lastReported = decision;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                // Both values are computed into locals first: the analyzer objects to work done in a logging
                // call's arguments, and it is right to, even when a guard makes it moot.
                var scope = ScopeName(decision.Scope);
                var tool = decision.ToolId ?? "none";
                LogScope(logger, scope, tool);
            }

            await machine.TryRecordEventAsync(
                new FocusEvent
                {
                    TsMs = machine.NowMs,
                    Process = window.ProcessName ?? "unknown",
                    Scope = decision.Scope,
                },
                ct).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        // A session nobody is attending has to be able to stop even after one attempt to stop it failed.
        await ResilientLoop.RunAsync(
            next: async token => await timer.WaitForNextTickAsync(token).ConfigureAwait(false),
            step: async token =>
            {
                if (trigger.Tick() is { Stop: true } stopped
                    && machine.State is SessionState.Recording or SessionState.Paused or SessionState.Suppressed)
                {
                    LogAutoStop(logger, stopped.Reason);
                    await machine.StopAsync(token).ConfigureAwait(false);
                }
            },
            onFailure: failure => LogLoopFailed(logger, "auto-stop", failure.GetType().Name),
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Not starting a session: nothing is on screen to show that capture is happening.")]
    private static partial void LogNoIndicator(ILogger logger);

    // The type and never the message: a store error can quote what it was asked to write (INV-10).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Loop} loop hit {Error} and carried on")]
    private static partial void LogLoopFailed(ILogger logger, string loop, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session started automatically: {Tool} took focus")]
    private static partial void LogAutoStart(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture started by the technician ({Tool})")]
    private static partial void LogManualStart(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session stopping automatically: {Reason}")]
    private static partial void LogAutoStop(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capture scope is now {Scope} ({Tool})")]
    private static partial void LogScope(ILogger logger, string scope, string tool);
}
