using System.Runtime.Versioning;
using System.Threading.Channels;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Intel;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
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
internal sealed partial class AutoSessionCoordinator
{
    private readonly Channel<ForegroundWindowInfo> _windows = Channel.CreateBounded<ForegroundWindowInfo>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly SessionMachine _machine;
    private readonly ScopePolicy _policy;
    private readonly SessionTrigger _trigger;
    private readonly Func<bool> _indicated;
    private readonly ILogger _logger;
    private readonly Func<string?>? _clipboard;
    private readonly Func<(bool LocalOnly, string? PolicyVersion)> _sessionPolicy;
    private readonly Func<bool>? _autoStart;
    private readonly TimeProvider _time;

    // The decision in force and what the timeline last said. Changed by the scope loop, by the auto-stop
    // timer and, when the policy moved, by whoever asks for the scope first. Never held across a wait.
    private readonly Lock _gate = new();
    private volatile Decided? _decided;
    private (CaptureScope Scope, string? ToolId)? _recorded;

    /// <param name="indicated">
    /// Whether anything is on screen saying capture is happening. A session that starts with no indicator is
    /// a recording nobody was told about (INV-4), and one that should never have begun is worse than one
    /// suppressed a moment later: it leaves a start time in the timeline that nobody witnessed.
    /// </param>
    /// <param name="sessionPolicy">
    /// The local-only flag and policy version a session starting now records (ST-047). Required: with no
    /// answer there is no flag to start a session under.
    /// </param>
    /// <param name="time">The clock the auto-stop timer runs on, so a test can run the grace out without waiting for it.</param>
    public AutoSessionCoordinator(
        SessionMachine machine,
        ScopePolicy policy,
        SessionTrigger trigger,
        Func<bool> indicated,
        ILogger logger,
        Func<(bool LocalOnly, string? PolicyVersion)> sessionPolicy,
        Func<string?>? clipboard = null,
        Func<bool>? autoStart = null,
        TimeProvider? time = null)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _indicated = indicated ?? throw new ArgumentNullException(nameof(indicated));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clipboard = clipboard;
        _sessionPolicy = sessionPolicy ?? throw new ArgumentNullException(nameof(sessionPolicy));
        _autoStart = autoStart;
        _time = time ?? TimeProvider.System;

        // The trigger follows the machine, whoever moved it. It used to hear only of the sessions it started
        // itself, so a session begun by the chord away from any remote tool never ran out its grace, and a
        // session stopped from the tray left the trigger believing one still ran, so the next remote-tool
        // focus started nothing.
        _machine.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// What the HUD shows about scope right now (Spec §5 S2), and what the capture loops ask before taking
    /// a frame. Made again for the window already in front when the policy has changed since it was made:
    /// turning "all windows" off, excluding an app or switching a tool off applies to the next frame, not
    /// to the next window the technician happens to move to (INV-5, INV-11). When nothing changed this is
    /// a reference comparison, so the capture loops pay nothing for it.
    /// </summary>
    public ScopeDecision? CurrentScope
    {
        get
        {
            var decided = _decided;
            if (decided is null || ReferenceEquals(decided.Options, _policy.Options))
            {
                return decided?.Decision;
            }

            lock (_gate)
            {
                return Redecided().Decision;
            }
        }
    }

    /// <summary>Called from the watcher's pump thread. Must not block, and does not.</summary>
    public void Observe(ForegroundWindowInfo window) => _windows.Writer.TryWrite(window);

    /// <summary>
    /// Decides the window in front again (ST-113): when the update's hold on new sessions is released,
    /// a tool that took focus meanwhile was refused its session and may still be in front.
    ///
    /// The window in front <i>now</i>, as the caller reads it, and only if it still exists: not the window
    /// last decided, which may have closed while the hold was in force (second review). It goes through the
    /// same queue as any foreground change, so every rule a focus change meets applies.
    /// </summary>
    /// <returns>False when there was no window in front, or it no longer exists.</returns>
    public bool Retry(ForegroundWindowInfo current, Func<nint, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(exists);
        if (current.IsNone || !exists(current.Handle))
        {
            return false;
        }

        Observe(current);
        return true;
    }

    /// <summary>The same, handled at once rather than queued, for the tests.</summary>
    internal async Task<bool> RetryAsync(ForegroundWindowInfo current, Func<nint, bool> exists, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(exists);
        if (current.IsNone || !exists(current.Handle))
        {
            return false;
        }

        await HandleAsync(current, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Starts a session for whatever is in front, for the Ctrl+Alt+R chord, a hotkey the service registers
    /// itself (ST-029). The tray has no start item.
    ///
    /// The coordinator is asked rather than the machine directly, because starting needs a remote tool to
    /// attribute the session to and only this knows what the technician is looking at. A window it does not
    /// recognise still starts a session, as <c>other</c>: a technician reaching for the chord has decided
    /// this is support work, and refusing because the tool is not in the registry would make the manual
    /// path useless exactly when the automatic one has already failed.
    /// </summary>
    public async Task<bool> StartFromForegroundAsync(CancellationToken ct = default)
    {
        if (!CanStart(_machine.State))
        {
            return false;
        }

        if (!_indicated())
        {
            LogNoIndicator(_logger);
            return false;
        }

        var scope = CurrentScope;
        var tool = new RemoteTool { Kind = scope?.Tool ?? RemoteToolKind.Other };
        var started = await StartUnderPolicyAsync(tool, Hint(_decided?.Window), ct).ConfigureAwait(false);
        if (started)
        {
            LogManualStart(_logger, scope?.ToolId ?? "unknown");
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
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
            onFailure: failure => LogLoopFailed(_logger, "scope", failure.GetType().Name),
            ct).ConfigureAwait(false);

        await ticking.ConfigureAwait(false);
    }

    /// <summary>
    /// Every session starts under the tenant's policy as it stands now (ST-047): local-only as the admin
    /// or the technician set it, and the policy's version written into the session for its audit log.
    /// </summary>
    private Task<bool> StartUnderPolicyAsync(RemoteTool tool, string? suggestedTicket, CancellationToken ct)
    {
        var (localOnly, policyVersion) = _sessionPolicy();
        return _machine.StartAsync(tool, localOnly, policyVersion, suggestedTicket, ct);
    }

    /// <summary>
    /// The ticket number in front of the technician as the session starts (ST-077): the window's title
    /// first, the clipboard only if the title says nothing, and the clipboard read here and only here —
    /// once per start, never kept (AC3). Digits come out; nothing else does (INV-10).
    /// </summary>
    private string? Hint(ForegroundWindowInfo? window) =>
        TicketHint.From(window?.Title, window?.BrowserTabTitle, null)
            ?? (_clipboard is null ? null : TicketHint.From(null, null, _clipboard()));

    /// <summary>
    /// One foreground change. Internal rather than private so the scope decision it leaves behind can be
    /// asserted directly; the class is already internal to this assembly and its tests.
    /// </summary>
    internal async Task HandleAsync(ForegroundWindowInfo window, CancellationToken ct)
    {
        var options = _policy.Options;
        var decision = _policy.Decide(window);
        var triggered = _trigger.Observe(window);

        // What is current, and what is worth writing down, are two different questions.
        //
        // This decision carries the window handle the screenshot capturer expects a frame to belong to,
        // and it used to be kept only when the scope or the tool id changed. Two windows of one remote
        // tool change neither — which is what a second customer session looks like — so the old handle
        // stayed, CaptureForegroundWindow(expected:) found the foreground window was not the one it had
        // been told to expect, and returned nothing. No error and no interval on the timeline: the
        // frames were simply missing from the half of the session that happened in the second window
        // (2026-09-21).
        bool worthRecording;
        lock (_gate)
        {
            _decided = new Decided(decision, window, options);
            worthRecording = TakeRecording(decision);
        }

        if (triggered.Start && CanStart(_machine.State))
        {
            var tool = new RemoteTool { Kind = decision.Tool ?? RemoteToolKind.Other };
            if (_autoStart is not null && !_autoStart())
            {
                // Settings → Capture "Auto-start" off (ST-080): the tool is in scope, the session waits for Ctrl+Alt+R.
                LogAutoStartOff(_logger, triggered.ToolId ?? "unknown");
            }
            else if (!_indicated())
            {
                LogNoIndicator(_logger);
            }
            else if (await StartUnderPolicyAsync(tool, Hint(window), ct).ConfigureAwait(false))
            {
                LogAutoStart(_logger, triggered.ToolId ?? "unknown");
            }
        }
        else if (triggered.Stop && IsRunning(_machine.State))
        {
            await AutoStopAsync(triggered, ct).ConfigureAwait(false);
        }

        // The trigger decides to start the moment the tool takes focus, whether or not a session then
        // begins. One that did not begin (auto-start off, nothing on screen to say capture is on, a
        // refusal, a session still finalizing) is not running, and saying so lets the next focus try again.
        if (triggered.Start && !IsRunning(_machine.State))
        {
            _ = _trigger.Follow(running: false);
        }

        // The timeline records where the technician went and whether it was in scope, even when no frame is
        // taken — that is what makes "clicks logged, no frames" visible in Review rather than a silent gap.
        // Still only on a real change: an entry per window switch would be noise in the one place a
        // technician goes to find out what happened.
        if (worthRecording)
        {
            await RecordFocusAsync(window, decision, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordFocusAsync(ForegroundWindowInfo window, ScopeDecision decision, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            // Both values are computed into locals first: the analyzer objects to work done in a logging
            // call's arguments, and it is right to, even when a guard makes it moot.
            var scope = ScopeName(decision.Scope);
            var tool = decision.ToolId ?? "none";
            LogScope(_logger, scope, tool);
        }

        await _machine.TryRecordEventAsync(
            new FocusEvent
            {
                TsMs = _machine.NowMs,
                Process = window.ProcessName ?? "unknown",
                Scope = decision.Scope,
            },
            ct).ConfigureAwait(false);
    }

    private async Task TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        // A session nobody is attending has to be able to stop even after one attempt to stop it failed.
        await ResilientLoop.RunAsync(
            next: async token => await timer.WaitForNextTickAsync(token).ConfigureAwait(false),
            step: TickOnceAsync,
            onFailure: failure => LogLoopFailed(_logger, "auto-stop", failure.GetType().Name),
            ct).ConfigureAwait(false);
    }

    /// <summary>One turn of the auto-stop timer. Internal so a test can run the grace out on its own clock.</summary>
    internal async Task TickOnceAsync(CancellationToken ct)
    {
        // A policy change re-decides the window in front at the next look at the scope, and the timeline
        // hears of it here, so Review shows where capture stopped even when nothing else moved.
        if (_decided is not null)
        {
            Decided now;
            bool worthRecording;
            lock (_gate)
            {
                now = Redecided();
                worthRecording = TakeRecording(now.Decision);
            }

            if (worthRecording)
            {
                await RecordFocusAsync(now.Window, now.Decision, ct).ConfigureAwait(false);
            }
        }

        // The window in front keeps the session when it is a remote tool or the one a session started by
        // hand was started in, so the grace runs from when the technician left it rather than from when
        // they arrived. A technician working in one window makes no foreground change to say so.
        if (_trigger.Tick(_decided?.Window) is { Stop: true } stopped && IsRunning(_machine.State))
        {
            await AutoStopAsync(stopped, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the session the trigger has given up on. The trigger already believes it stopped, so a stop
    /// that failed would leave a session nothing tries to end again. It is told the session still runs,
    /// and the grace starts over rather than never.
    /// </summary>
    private async Task AutoStopAsync(TriggerDecision stopped, CancellationToken ct)
    {
        LogAutoStop(_logger, stopped.Reason);
        try
        {
            _ = await _machine.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (IsRunning(_machine.State))
            {
                _ = _trigger.Follow(running: true, stopped.ToolId);
            }
        }
    }

    /// <summary>Raised on whichever thread moved the machine. The trigger takes it under its own lock and returns.</summary>
    private void OnStateChanged(CaptureStateSnapshot snapshot)
    {
        var running = snapshot.State is CaptureStates.Recording or CaptureStates.Paused or CaptureStates.Suppressed;
        var decided = running ? _decided : null;
        _ = _trigger.Follow(running, decided?.Decision.ToolId, decided?.Window);
    }

    /// <summary>The decision in force, made again if the policy changed since it was made. Call under the gate.</summary>
    private Decided Redecided()
    {
        var decided = _decided!;
        var options = _policy.Options;
        if (ReferenceEquals(decided.Options, options))
        {
            return decided;
        }

        var next = new Decided(_policy.Decide(decided.Window), decided.Window, options);
        _decided = next;
        return next;
    }

    /// <summary>Whether this decision is news to the timeline, and if it is, marks it told. Call under the gate.</summary>
    private bool TakeRecording(ScopeDecision decision)
    {
        if (_recorded is { } recorded && recorded.Scope == decision.Scope && recorded.ToolId == decision.ToolId)
        {
            return false;
        }

        _recorded = (decision.Scope, decision.ToolId);
        return true;
    }

    private static bool CanStart(SessionState state) =>
        state is SessionState.Idle or SessionState.DraftReady or SessionState.DraftFailed or SessionState.DraftPending;

    private static bool IsRunning(SessionState state) =>
        state is SessionState.Recording or SessionState.Paused or SessionState.Suppressed;

    /// <summary>A scope decision, the window it is about, and the policy it was made under.</summary>
    private sealed record Decided(ScopeDecision Decision, ForegroundWindowInfo Window, ScopeOptions Options);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Not starting a session: nothing is on screen to show that capture is happening.")]
    private static partial void LogNoIndicator(ILogger logger);

    // The type and never the message: a store error can quote what it was asked to write (INV-10).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Loop} loop hit {Error} and carried on")]
    private static partial void LogLoopFailed(ILogger logger, string loop, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Tool} took focus; auto-start is off, so the session waits for the hotkey")]
    private static partial void LogAutoStartOff(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session started automatically: {Tool} took focus")]
    private static partial void LogAutoStart(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture started by the technician ({Tool})")]
    private static partial void LogManualStart(ILogger logger, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session stopping automatically: {Reason}")]
    private static partial void LogAutoStop(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capture scope is now {Scope} ({Tool})")]
    private static partial void LogScope(ILogger logger, string scope, string tool);
}
