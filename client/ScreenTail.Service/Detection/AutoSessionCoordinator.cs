using System.Runtime.Versioning;
using System.Threading.Channels;
using ScreenTail.Core.Detection;
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
internal sealed partial class AutoSessionCoordinator(
    SessionMachine machine,
    ScopePolicy policy,
    SessionTrigger trigger,
    ILogger logger)
{
    private readonly Channel<ForegroundWindowInfo> _windows = Channel.CreateBounded<ForegroundWindowInfo>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private ScopeDecision? _lastReported;

    /// <summary>What the HUD shows about scope right now (Spec §5 S2).</summary>
    public ScopeDecision? CurrentScope => _lastReported;

    /// <summary>Called from the watcher's pump thread. Must not block, and does not.</summary>
    public void Observe(ForegroundWindowInfo window) => _windows.Writer.TryWrite(window);

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

        try
        {
            await foreach (var window in _windows.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await HandleAsync(window, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await ticking.ConfigureAwait(false);
    }

    private async Task HandleAsync(ForegroundWindowInfo window, CancellationToken ct)
    {
        var decision = policy.Decide(window);
        var triggered = trigger.Observe(window);

        if (triggered.Start && machine.State is SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed)
        {
            var tool = new RemoteTool { Kind = decision.Tool ?? RemoteToolKind.Other };
            if (await machine.StartAsync(tool, ct: ct).ConfigureAwait(false))
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
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (trigger.Tick() is { Stop: true } stopped
                    && machine.State is SessionState.Recording or SessionState.Paused or SessionState.Suppressed)
                {
                    LogAutoStop(logger, stopped.Reason);
                    await machine.StopAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session started automatically: {Tool} took focus")]
    private static partial void LogAutoStart(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session stopping automatically: {Reason}")]
    private static partial void LogAutoStop(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capture scope is now {Scope} ({Tool})")]
    private static partial void LogScope(ILogger logger, string scope, string tool);
}
