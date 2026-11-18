using System.Runtime.Versioning;
using System.Threading.Channels;
using ScreenTrail.Core.Detection;
using ScreenTrail.Core.Intel;
using ScreenTrail.Core.Privacy;
using ScreenTrail.Core.Sessions;
using ScreenTrail.Shared.Ipc;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Service.Detection;

/// <summary>
/// Joins the foreground watcher to the session machine (ST-023): a remote tool takes focus and a session
/// starts; the technician stays away from one for the grace period and it stops.
///
/// The watcher raises its event on the thread pumping the WinEvent hook, and that thread must stay free:
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
    private readonly Func<ForegroundWindowInfo?>? _latest;
    private readonly RecentRemoteTool _recent;
    private readonly Func<nint, bool> _exists;

    // The window a tray start is starting in, for the trigger to keep the session by (OnStateChanged), set
    // only for the length of that start: the window in front then is the menu, not the remote session.
    private volatile StartedIn? _startedIn;

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
    /// <param name="latest">
    /// The watcher's last look at the window in front, reported or held back while its title settles
    /// (<see cref="ForegroundChangeFilter.Latest"/>). When it shows the decided window under a title nobody
    /// has decided on, <see cref="CurrentScope"/> decides again on it before answering (INV-6). Null keeps
    /// the decision on reports alone.
    /// </param>
    /// <param name="exists">
    /// Whether a window handle still names a window, so the tray's start does not begin a session in a
    /// remote tool window that has closed. Null takes every remembered window as open (tests).
    /// </param>
    /// <param name="recent">The remote tool window most recently in front, for the tray's start. Null keeps one on the coordinator's clock.</param>
    public AutoSessionCoordinator(
        SessionMachine machine,
        ScopePolicy policy,
        SessionTrigger trigger,
        Func<bool> indicated,
        ILogger logger,
        Func<(bool LocalOnly, string? PolicyVersion)> sessionPolicy,
        Func<string?>? clipboard = null,
        Func<bool>? autoStart = null,
        TimeProvider? time = null,
        Func<ForegroundWindowInfo?>? latest = null,
        Func<nint, bool>? exists = null,
        RecentRemoteTool? recent = null)
    {
        _latest = latest;
        _exists = exists ?? (_ => true);
        _recent = recent ?? new RecentRemoteTool(time);
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
    /// to the next window the technician happens to move to (INV-5, INV-11). Made again, too, when the
    /// window decided on has since been retitled and the watcher is still holding the new title back to
    /// let it settle: a tab that became an excluded page is not photographed under the old tab's decision
    /// for those 250 ms (INV-6, <see cref="UndecidedTitle"/>). Such a title can only tighten the decision;
    /// it loosens when the report arrives. When nothing changed this is a reference
    /// comparison and a title comparison, so the capture loops pay almost nothing for it.
    /// </summary>
    public ScopeDecision? CurrentScope
    {
        get
        {
            var decided = _decided;
            if (decided is null
                || (ReferenceEquals(decided.Options, _policy.Options) && UndecidedTitle.Newer(decided.Window, _latest?.Invoke()) is null))
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
    /// itself (ST-029). The tray's Start capture is <see cref="StartFromTrayAsync"/>, by the same start.
    ///
    /// The coordinator is asked rather than the machine directly, because starting needs a remote tool to
    /// attribute the session to and only this knows what the technician is looking at. A window it does not
    /// recognise still starts a session, as <c>other</c>: a technician reaching for the chord has decided
    /// this is support work, and refusing because the tool is not in the registry would make the manual
    /// path useless exactly when the automatic one has already failed.
    /// </summary>
    public async Task<bool> StartFromForegroundAsync(CancellationToken ct = default) =>
        await StartByHandAsync(() => (_decided?.Window, CurrentScope), startedIn: false, ct).ConfigureAwait(false) == HandStartOutcome.Started;

    /// <summary>
    /// The tray's Start capture (ST-071, Spec §5 S1 as built v0.7.2, contract v13): the session Ctrl+Alt+R
    /// would have started in the remote tool window most recently in front, by the same start as the chord.
    ///
    /// The menu is in front as it is chosen, so the window in front says nothing about where the technician
    /// was working. The remote tool window last seen in front (<see cref="RecentRemoteTool"/>) is used
    /// instead: within the last few minutes, still open, and still a remote tool under the policy in force
    /// now. With none, nothing starts and the answer says to open the remote session first; a session
    /// attributed to <c>other</c> and about nothing would be a draft nobody asked for. The session is kept
    /// while that window is in front, as one started by the chord in it is.
    /// </summary>
    public async Task<CommandResult> StartFromTrayAsync(StartCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var outcome = await StartByHandAsync(
            () => _recent.Pick(_policy, _exists) is { } recent ? (recent.Window, recent.Decision) : null,
            startedIn: true,
            ct).ConfigureAwait(false);
        return HandStart.Result(command.RequestId, outcome);
    }

    /// <summary>
    /// A start by hand, the chord's or the tray's: the rules in <see cref="HandStart"/>'s order, then the
    /// indicator (INV-4), then the session under the tenant's policy as it stands now (ST-047), attributed
    /// to the window's tool, <c>other</c> when the chord was pressed over a window no registry knows.
    /// </summary>
    /// <param name="target">The window to start in and its scope, read once the hold and the state allow a start; null for none.</param>
    /// <param name="startedIn">
    /// Whether the session is kept while <paramref name="target"/>'s window is in front, rather than the
    /// window in front as the session starts. The tray's start says so, since the menu is in front then.
    /// </param>
    private async Task<HandStartOutcome> StartByHandAsync(Func<(ForegroundWindowInfo? Window, ScopeDecision? Scope)?> target, bool startedIn, CancellationToken ct)
    {
        if (HandStart.Before(_machine.StartsHeld, _machine.State) is { } refused)
        {
            return refused;
        }

        if (target() is not { } aim)
        {
            LogNoRecentTool(_logger);
            return HandStartOutcome.NoRemoteSession;
        }

        if (!_indicated())
        {
            LogNoIndicator(_logger);
            return HandStartOutcome.NotIndicated;
        }

        var tool = new RemoteTool { Kind = aim.Scope?.Tool ?? RemoteToolKind.Other };
        _startedIn = startedIn ? new StartedIn(aim.Scope?.ToolId, aim.Window) : null;
        bool started;
        try
        {
            started = await StartUnderPolicyAsync(tool, Hint(aim.Window), ct).ConfigureAwait(false);
        }
        finally
        {
            _startedIn = null;
        }

        if (started)
        {
            LogManualStart(_logger, aim.Scope?.ToolId ?? "unknown");
            return HandStartOutcome.Started;
        }

        return _machine.StartsHeld ? HandStartOutcome.Held : HandStartOutcome.Busy;
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
        // stays in force for every window that follows, and if that decision was "remote tool", typing
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
    /// first, the clipboard only if the title says nothing, and the clipboard read here and only here,
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
        // tool change neither (which is what a second customer session looks like), so the old handle
        // stayed, CaptureForegroundWindow(expected:) found the foreground window was not the one it had
        // been told to expect, and returned nothing. No error and no interval on the timeline: the
        // frames were simply missing from the half of the session that happened in the second window
        // (2026-09-21).
        bool worthRecording;
        lock (_gate)
        {
            _decided = new Decided(decision, window, options, window);
            worthRecording = TakeRecording(decision);
        }

        // The window the tray's Start capture would start in, once the menu has taken the front.
        _recent.Saw(window, decision);

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
        // taken. That is what makes "clicks logged, no frames" visible in Review rather than a silent gap.
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

            // A remote tool still in front stays the recent one, however long nothing moves.
            _recent.Saw(now.Window, now.Decision);

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

        // A tray start names the window it started in; any other start is in the window in front.
        if (running && _startedIn is { } startedIn)
        {
            _ = _trigger.Follow(running, startedIn.ToolId, startedIn.Window);
            return;
        }

        var decided = running ? _decided : null;
        _ = _trigger.Follow(running, decided?.Decision.ToolId, decided?.Window);
    }

    /// <summary>
    /// The decision in force, made again if the policy changed since it was made or the window it is about
    /// has a newer title the watcher has not reported yet. Call under the gate.
    ///
    /// On a title not reported yet the decision can only tighten: the stricter of the one in force and the
    /// new title's is kept (<see cref="ScopeDecision.Stricter"/>), and it loosens when the report arrives
    /// (<see cref="HandleAsync"/>) or the window shows the reported title again. A title on its way
    /// somewhere may not be photographed on the strength of where it is passing through.
    ///
    /// The timeline hears of a change made here at the next auto-stop tick (<see cref="TickOnceAsync"/>)
    /// or at the report, whichever comes first, not once per rewrite of the title.
    /// </summary>
    private Decided Redecided()
    {
        var decided = _decided!;
        var options = _policy.Options;
        var window = UndecidedTitle.Newer(decided.Window, _latest?.Invoke()) ?? decided.Window;
        if (ReferenceEquals(decided.Options, options) && ReferenceEquals(window, decided.Window))
        {
            return decided;
        }

        var decision = _policy.Decide(window);
        if (!window.SameAs(decided.Reported))
        {
            decision = ScopeDecision.Stricter(decided.Decision, decision);
        }

        var next = new Decided(decision, window, options, decided.Reported);
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

    /// <summary>
    /// A scope decision, the window it is about, the policy it was made under, and the last look at that
    /// window the watcher reported (the same as <paramref name="Window"/> unless a retitle is still settling).
    /// </summary>
    private sealed record Decided(ScopeDecision Decision, ForegroundWindowInfo Window, ScopeOptions Options, ForegroundWindowInfo Reported);

    /// <summary>The window a tray start is starting in, and its tool's id.</summary>
    private sealed record StartedIn(string? ToolId, ForegroundWindowInfo? Window);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Not starting a session from the tray: no remote tool window has been in front recently.")]
    private static partial void LogNoRecentTool(ILogger logger);

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
