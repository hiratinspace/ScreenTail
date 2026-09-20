using System.Reflection;
using System.Runtime.Versioning;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Input;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Platform.Ipc;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Capture;
using ScreenTail.Service.Detection;
using ScreenTail.Service.Input;
using ScreenTail.Service.Intel;
using ScreenTail.Service.Privacy;
using ScreenTail.Service.Speech;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>
/// The per-user capture service (ADR-0003): opens the encrypted store, recovers any session a crash left
/// behind (ST-020), publishes this run's IPC token, and serves the pipe with the state machine behind it.
/// Capture sources (hooks, screenshots, speech) and drafting arrive with their tickets.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class CaptureHost(ILogger<CaptureHost> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    /// <summary>
    /// Set by the <c>erase_all_local_data</c> command, acted on after everything is closed (INV-12).
    ///
    /// Deleting the store from under the loops that are writing to it would race every one of them, and on
    /// Windows an open database file cannot be deleted at all. So the command asks the service to stop and
    /// the erase happens once the last handle is gone, which is also why the UI sees the pipe drop:
    /// the thing it was talking to is being deleted.
    /// </summary>
    private volatile bool _eraseOnShutdown;

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            if (_eraseOnShutdown)
            {
                // Everything above is disposed by now: the store is closed and its file can be removed.
                var deleted = LocalDataEraser.Erase(DataDirectory);
                LogErased(logger, deleted.Count);
            }
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var serviceExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the service executable.");

        await using var store = await SqliteSessionStore.OpenAsync(
            Path.Combine(DataDirectory, "store.db"),
            new DpapiKeyProvider(DpapiKeyProvider.DefaultKeyFilePath),
            ct: stoppingToken).ConfigureAwait(false);

        // Recover before serving, so the first UI to connect sees the outcome, not the orphan.
        // The sources need the capturer and the scope coordinator, which are built further down; the
        // machine has to exist before either, so it gets a holder that is filled in once they do.
        var sources = new DeferredCaptureSources();
        // ST-060: the bundle is assembled for real when a session ends, and the only missing step is a
        // provider to send it to. Building it from today means the selection rules run against real
        // sessions on real hardware before there is anything at stake in them.
        // ST-064: work that has to reach the network, kept until it does. The sender is not wired yet —
        // ST-063 supplies the provider — so nothing leaves the machine; what exists now is the queue, so
        // that a draft owed while offline is still owed after a restart rather than lost at finalize.
        var outbox = new Core.Outbox.Outbox(
            store,
            (item, _) => Task.FromResult(SendOutcome.Retry("No summarization provider is configured yet.")),
            TimeProvider.System);
        var drafter = new BundlingDrafter(store, logger, outbox);
        // One engine for what is seen and what is said. Two would drift the day a tenant's own patterns
        // are loaded into one of them, and speech would go on being checked against the defaults.
        var redactionEngine = new RedactionEngine();
        await using var machine = new SessionMachine(
            store,
            sources,
            drafter,
            options: new SessionMachineOptions { Scrubber = redactionEngine });
        var recovered = await machine.RecoverAsync(stoppingToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            LogRecovered(logger, recovered);
        }

        var token = IpcToken.Generate();
        IpcTokenFile.Write(IpcTokenFile.DefaultPath, token);

        var verifier = new WindowsClientVerifier(serviceExecutable);
        var pipeName = WindowsPipeFactory.PipeNameForCurrentUser();

        // ST-046: every HTTP request the client makes is built through this, so INV-8 is enforced by the
        // composition root rather than by convention. Nothing in the service makes one yet; the guard is
        // installed now so that the first thing that does cannot accidentally be the exception.
        var egress = new EgressGuard(new EgressPolicy());

        // Filled in below, once the pieces it reports on exist. The controller only ever calls it on a
        // request, by which time everything is wired.
        Func<DiagnosticsReported> diagnostics = () => Diagnostics(version, egress, null, null, null);
        var controller = new CaptureController(
            machine,
            new WindowsCapabilityProbe(),
            store,
            () => diagnostics(),
            _ =>
            {
                _eraseOnShutdown = true;
                lifetime.StopApplication();
                return Task.FromResult(true);
            });
        await using var server = new IpcServer(
            WindowsPipeFactory.ForCurrentUser(pipeName),
            token,
            verifier,
            controller,
            store,
            version);
        Array.Clear(token);

        machine.StateChanged += snapshot => _ = server.BroadcastAsync(new StateChanged { State = snapshot }, stoppingToken);
        server.Start();

        var mode = verifier.ServiceIsSigned ? "signed-publisher" : "dev-same-directory";
        var state = machine.State.ToWire();
        LogStarted(logger, IpcContract.Version, mode, state);

        // INV-10: capability messages describe Windows settings, never anything captured.
        var capabilities = new WindowsCapabilityProbe().Probe();
        foreach (var problem in capabilities.Checks.Where(c => c.State != CapabilityState.Ok))
        {
            LogCapability(logger, problem.Capability.ToString(), problem.State.ToString(), problem.Message);
        }

        // ST-022: watch the foreground so ST-023 can decide scope and ST-040 can suppress. Only the window's
        // identity is logged, never its title (INV-10).
        await using var foreground = new WindowsForegroundWatcher();
        // Passed as separate values rather than a formatted string: nothing is built unless the log is on,
        // and the title is reduced to its length here, never its text.
        foreground.Changed += window => LogForeground(
            logger,
            window.ProcessName ?? "unknown",
            window.ProcessId,
            window.ClassName,
            window.Title.Length,
            window.IsElevated);

        // ST-023: the registry decides what counts as a support session and what may be photographed
        // alongside one (INV-5). A registry that fails validation stops the service rather than falling back
        // to something permissive — the safe default here is to capture nothing, not to guess.
        var registry = RemoteToolRegistry.Load(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json"),
            stoppingToken).ConfigureAwait(false));
        LogRegistry(logger, registry.Version, registry.Tools.Count, registry.BrowserPatterns.Count, registry.Grace.TotalSeconds);

        // ST-043: the shipped exclusions — password managers, banking tabs, credential prompts. Loaded
        // beside the registry and refused the same way if it will not parse: a privacy list that silently
        // half-loaded would stop excluding whatever came after the broken rule, and nothing would say so.
        var exclusions = ExclusionList.Load(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Registry", "exclusions-default.json"),
            stoppingToken).ConfigureAwait(false));
        LogExclusions(logger, exclusions.Version, exclusions.ExcludedApplications, exclusions.Rules);

        var policy = new ScopePolicy(registry, new ScopeOptions { Exclusions = exclusions });
        var coordinator = new AutoSessionCoordinator(machine, policy, new SessionTrigger(policy), logger);
        foreground.Changed += coordinator.Observe;

        await foreground.StartAsync(stoppingToken).ConfigureAwait(false);
        LogForegroundMode(logger, foreground.UsingHook ? "event hook" : "polling");
        var coordinating = coordinator.RunAsync(stoppingToken);

        // ST-024: hooks run for the life of the service; the state machine decides whether what they see is
        // recorded (INV-6). Draining on a timer keeps the callbacks free of everything but a buffer write.
        await using var hooks = new WindowsInputHooks();
        await hooks.StartAsync(stoppingToken).ConfigureAwait(false);
        LogHooks(logger, hooks.Installed);

        // ST-025: clicks become events always, and screenshots only where scope allows.
        using var capturer = new ScreenshotCapturer();
        var capture = new ClickCaptureLoop(
            machine,
            new WindowsInputHooksAccessor(hooks),
            capturer,
            () => coordinator.CurrentScope,
            logger);
        var capturing = capture.RunAsync(stoppingToken);

        // ST-026: clicks miss everything the technician reads rather than does — a dialog appearing while
        // they watch, a service finally starting. This looks once a second and only pays for a full capture
        // when the screen actually changed.
        var scenes = new SceneSampleLoop(machine, capturer, () => coordinator.CurrentScope, logger);
        var sampling = scenes.RunAsync(stoppingToken);

        // ST-029: "mark moment" is the only frame the technician asks for by name, so it goes around the
        // debounce and the sampler both. Until this was wired the marker was written and the frame it
        // pointed at never existed.
        sources.Use(new WindowsCaptureSources(
            capturer,
            () => coordinator.CurrentScope,
            () =>
            {
                capture.Reset();
                scenes.Reset();
            },
            logger));

        // ST-029: the global chords. A conflict costs a shortcut, not a capture service, so it is reported
        // and the rest carry on.
        var router = new HotkeyRouter(machine, coordinator.StartFromForegroundAsync);
        await using var hotkeys = new Hotkeys();
        hotkeys.Pressed += action => _ = Task.Run(() => router.InvokeAsync(action, stoppingToken), stoppingToken);
        await hotkeys.StartAsync(stoppingToken).ConfigureAwait(false);
        foreach (var conflict in hotkeys.Conflicts)
        {
            LogHotkeyConflict(logger, conflict.Hotkey.ToString(), conflict.Reason, conflict.Suggestion?.ToString() ?? "none");
        }

        // ST-041: without this, every staged frame stays redaction_pending and is deleted at finalize —
        // the session would end with no screenshots at all. This is also the only thing allowed to read a
        // pending frame or to clear the flag, so INV-1 rests on it.
        var recogniser = new WindowsOcrRecogniser();
        LogOcr(logger, recogniser.Available, recogniser.Language ?? "none");
        var redaction = new RedactionWorker(store, recogniser, new WindowsFrameMasker(), redactionEngine);
        redaction.BacklogChanged += machine.ReportPendingRedactions;

        // Without this the login heuristic fires into nothing: the frame is marked sensitive and the next
        // click still screenshots the same password prompt. INV-6 is about capture stopping, not about an
        // event being raised.
        var sensitive = new SensitiveContextGuard(machine);
        sensitive.Failed += failure => LogGuardFailed(logger, "sensitive-context", failure.GetType().Name);
        redaction.SensitiveContextSeen += sensitive.Seen;
        var guarding = sensitive.RunAsync(stoppingToken);

        // ST-040: the other half of INV-6's sensitive-field rule, and the faster half. ST-041 reads the
        // screen and takes seconds; this asks Windows directly the moment focus moves. Each covers what the
        // other misses — a field with no automation peer, a prompt whose words give it away.
        await using var focus = new WindowsFocusWatcher();
        using var focusedField = new WindowsFocusedFieldProbe();
        using var password = new PasswordFieldGuard(machine, focusedField);
        password.Failed += failure => LogGuardFailed(logger, "password-field", failure.GetType().Name);
        focus.FocusMoved += password.FocusMoved;
        await focus.StartAsync(stoppingToken).ConfigureAwait(false);
        LogFocusMode(logger, focus.UsingHook ? "event hook" : "polling");
        var watchingFields = password.RunAsync(stoppingToken);

        var redacting = redaction.RunAsync(stoppingToken);

        // ST-027: the technician's narration, transcribed on this machine and never anywhere else
        // (INV-9). The model is fetched on first use through the egress guard, which is the only network
        // request the capture service makes; audio itself never leaves.
        //
        // Nothing here can stop the session. A machine with no microphone, a blocked one, or a model that
        // will not download all end the same way: clicks and screenshots are still recorded and the note
        // is written without narration.
        await using var microphone = new WindowsMicrophone();
        await using var whisper = new WhisperRecogniser(new ModelDownload(new HttpClient(egress)));
        var narration = new NarrationRecorder(
            microphone,
            whisper,
            (segment, ct) => machine.TryAppendTranscriptAsync(segment, ct));
        LogMicrophone(logger, microphone.DeviceName ?? "none", whisper.ModelName);

        var preparing = Task.Run(
            async () =>
            {
                if (microphone.DeviceName is not null && !await whisper.PrepareAsync(stoppingToken).ConfigureAwait(false))
                {
                    LogNoModel(logger, whisper.ModelName);
                }
            },
            stoppingToken);
        var listening = narration.RunAsync(stoppingToken);

        // Everything the "What's being captured right now?" panel shows now has a live source (ST-085).
        // Counts and states only: the scope decision names a process, never a window title (INV-10).
        diagnostics = () => Diagnostics(
            version,
            egress,
            coordinator.CurrentScope?.Reason,
            redaction.Progress,
            capture.DroppedOutOfScope,
            narration.Microphone);

        // INV-12: retention runs at start and hourly. ST-047 feeds the tenant's retention days into the options.
        var retention = new RetentionJob(store, TimeProvider.System, new RetentionOptions(), () => machine.SessionId);

        // Drained on a slow timer rather than continuously: everything in it is minutes-scale work that a
        // technician is not waiting on, and a tight loop on a laptop is a battery complaint.
        var draining = DrainOutboxAsync(outbox, logger, stoppingToken);
        using var hourly = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                var purged = await retention.RunAsync(stoppingToken).ConfigureAwait(false);
                if (purged > 0)
                {
                    LogRetention(logger, purged);
                }
            }
            while (await hourly.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(coordinating, capturing, sampling, redacting, guarding, watchingFields, listening, preparing, draining).ConfigureAwait(false);
        LogStopping(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention removed raw data from {Count} session(s)")]
    private static partial void LogRetention(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service started: IPC contract v{IpcVersion}, client verification {Mode}, state {State}")]
    private static partial void LogStarted(ILogger logger, int ipcVersion, string mode, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "OCR available: {Available} ({Language})")]
    private static partial void LogOcr(ILogger logger, bool available, string language);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote-tool registry {Version}: {Tools} tools, {Patterns} browser patterns, {Grace}s grace")]
    private static partial void LogRegistry(ILogger logger, string version, int tools, int patterns, double grace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Input hooks installed: {Installed}")]
    private static partial void LogHooks(ILogger logger, bool installed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Foreground: {Process}#{ProcessId} class={Class} title={TitleLength} chars elevated={Elevated}")]
    private static partial void LogForeground(ILogger logger, string process, int processId, string @class, int titleLength, bool elevated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Foreground detection using {Mode}")]
    private static partial void LogForegroundMode(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exclusions {Version}: {Applications} applications, {Rules} rules")]
    private static partial void LogExclusions(ILogger logger, string version, int applications, int rules);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotkey {Hotkey} is unavailable: {Reason} Suggested instead: {Suggestion}")]
    private static partial void LogHotkeyConflict(ILogger logger, string hotkey, string reason, string suggestion);

    // The type and never the message: a store error can quote what it was asked to write (INV-10).
    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Guard} guard hit {Error} and carried on")]
    private static partial void LogGuardFailed(ILogger logger, string guard, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password-field detection using {Mode}")]
    private static partial void LogFocusMode(ILogger logger, string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capability {Capability} is {State}: {Message}")]
    private static partial void LogCapability(ILogger logger, string capability, string state, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} session(s) left behind by a previous run; marked partial and finalized")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service stopping")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delete everything: removed {Count} file(s) and the tokens directory")]
    private static partial void LogErased(ILogger logger, int count);

    /// <summary>
    /// Works the outbox until the service stops.
    ///
    /// One item per tick and a long tick: nothing in the queue is something a technician is waiting on,
    /// and a queue that spins costs battery on a machine that is also capturing.
    /// </summary>
    private static async Task DrainOutboxAsync(Core.Outbox.Outbox outbox, ILogger logger, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                _ = await outbox.DrainAsync(ct).ConfigureAwait(false);
                var waiting = await outbox.WaitingAsync(ct).ConfigureAwait(false);
                if (waiting.Total > 0)
                {
                    LogOutbox(logger, waiting.Drafts, waiting.Publishes, waiting.Uncertain);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox: {Drafts} draft(s) and {Publishes} publish(es) waiting, {Uncertain} needing a look")]
    private static partial void LogOutbox(ILogger logger, int drafts, int publishes, int uncertain);

    [LoggerMessage(Level = LogLevel.Information, Message = "Microphone: {Device}; speech model {Model}")]
    private static partial void LogMicrophone(ILogger logger, string device, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Speech model {Model} could not be prepared; this session has no narration")]
    private static partial void LogNoModel(ILogger logger, string model);

    /// <summary>
    /// What the diagnostics panel shows, assembled from the things only this process can see.
    ///
    /// Every field is a state, a count or a device name. The active window appears as the scope decision's
    /// own words — a process name and what is being done about it — because this text goes on a clipboard
    /// and into tickets, and a window title there is a customer's business in someone's ticket (INV-10).
    /// </summary>
    private static DiagnosticsReported Diagnostics(
        string version,
        EgressGuard egress,
        string? scope,
        RedactionProgress? redaction,
        long? keystrokesDropped,
        string? microphone = null)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new DiagnosticsReported
        {
            Scope = scope ?? "Not capturing — no remote session in front",
            Microphone = microphone, // A device name, never audio (INV-10).
            Suppression = null, // The HUD carries this from the capture state; the panel echoes it in ST-081.
            RedactionBacklog = 0,
            FramesDropped = (redaction?.Unread ?? 0) + (redaction?.Unreadable ?? 0),
            KeystrokesDropped = keystrokesDropped ?? 0,
            EgressBlocked = egress.Blocked,
            LocalOnly = new EgressPolicy().Settings.LocalOnly,
            PolicyVersion = "local", // ST-047 replaces this with the tenant's policy version.
            CpuPercent = 0,
            WorkingSetBytes = self.WorkingSet64,
            ServiceVersion = version,
        };
    }
}
