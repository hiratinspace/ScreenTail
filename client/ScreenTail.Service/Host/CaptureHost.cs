using System.Reflection;
using System.Runtime.Versioning;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Input;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Review;
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
                // The marker was written before the UI was told yes, so a file we cannot delete here
                // leaves the erasure owed and the next start finishes it.
                var erased = LocalDataEraser.Erase(DataDirectory);
                LogErased(logger, erased.Deleted.Count, erased.Complete);
            }
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var serviceExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the service executable.");

        // Before the store is opened, because an erasure that was promised and did not finish must not be
        // undone by something reopening what is left of it.
        if (LocalDataEraser.EraseIfPending(DataDirectory) is { } owed)
        {
            LogErased(logger, owed.Deleted.Count, owed.Complete);
        }

        await using var store = await SqliteSessionStore.OpenAsync(
            Path.Combine(DataDirectory, "store.db"),
            new DpapiKeyProvider(DpapiKeyProvider.DefaultKeyFilePath),
            ct: stoppingToken).ConfigureAwait(false);

        // Recover before serving, so the first UI to connect sees the outcome, not the orphan.
        // The sources need the capturer and the scope coordinator, which are built further down; the
        // machine has to exist before either, so it gets a holder that is filled in once they do.
        var sources = new DeferredCaptureSources();

        // Where this deployment's backend is, and what this device calls itself.
        //
        // Environment variables, and deliberately not a settings system. ST-047 and ST-081 own settings
        // and an enrolment flow owns the token (ST-010 has no issuing endpoint yet, so this one is
        // issued by hand). What this is for is making the path real today: without a backend address
        // nothing can be sent, and inventing a configuration format here would be inventing the thing
        // two other tickets are going to replace.
        //
        // Unset is the ordinary case and not an error. The queue keeps the work, Review says the session
        // could not be drafted, and the screenshots and transcript are still there (Spec §5 S3).
        var backend = Uri.TryCreate(Environment.GetEnvironmentVariable("SCREENTAIL_BACKEND"), UriKind.Absolute, out var url)
            ? url
            : null;

        // ST-046: every HTTP request the client makes is built through this, so INV-8 is enforced by the
        // composition root rather than by convention. One policy for the guard and for what diagnostics
        // reports about it -- two meant the panel read local-only off a throwaway object and told a
        // customer "local-only: no" whatever the guard was actually doing (2026-09-19 review).
        //
        // The model hosts are named because the transcriber cannot fetch its model otherwise. The
        // backend host is named from the address that was configured, so the allowlist is what an
        // operator asked for rather than whatever a server later claims to be.
        var egressPolicy = new EgressPolicy(new EgressSettings
        {
            ModelHosts = SpeechModels.Hosts,
            BackendHosts = backend is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { backend.Host },
        });
        var egress = new EgressGuard(egressPolicy);

        // ST-060 built the bundle, ST-063 built the endpoint, ST-064 built the queue, and between them
        // sat a stub that returned "no summarization provider is configured yet" -- so no session has
        // ever produced a note. This is that step.
        //
        // With no backend configured the queue behaves exactly as it did: the work is kept, and the
        // reason a technician sees names the thing that is missing.
        var draftHttp = new HttpClient(egress) { BaseAddress = backend };
        var sender = new DraftSender(draftHttp, store, () => Environment.GetEnvironmentVariable("SCREENTAIL_DEVICE_TOKEN"));
        var outbox = new Core.Outbox.Outbox(
            store,
            (item, ct) => backend is null
                ? Task.FromResult(SendOutcome.Retry(BundlingDrafter.NoProviderReason))
                : sender.SendAsync(item, ct),
            TimeProvider.System);

        var drafter = new BundlingDrafter(store, logger, outbox);
        // One engine for what is seen and what is said. Two would drift the day a tenant's own patterns
        // are loaded into one of them, and speech would go on being checked against the defaults.
        var redactionEngine = new RedactionEngine();

        // Where a captured frame waits to be read (ADR-0006). One queue, shared by the thing that fills
        // it and the worker that drains it, so an unredacted frame never reaches disk.
        var pending = new PendingFrames();
        await using var machine = new SessionMachine(
            store,
            sources,
            drafter,
            options: new SessionMachineOptions { Scrubber = redactionEngine, Pending = pending });
        var recovered = await machine.RecoverAsync(stoppingToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            LogRecovered(logger, recovered);
        }

        var token = IpcToken.Generate();
        IpcTokenFile.Write(IpcTokenFile.DefaultPath, token);

        var verifier = new WindowsClientVerifier(serviceExecutable);
        var pipeName = WindowsPipeFactory.PipeNameForCurrentUser();

        // Filled in below, once the pieces it reports on exist. The controller only ever calls it on a
        // request, by which time everything is wired.
        Func<DiagnosticsReported> diagnostics = () => Diagnostics(version, egress, egressPolicy, null, null, null);
        // What the UI reports about the pill being on screen, and what the indicator guard reads. A
        // connection is not a pill (2026-09-20 review).
        var indicators = new IndicatorReports();
        var controller = new CaptureController(
            machine,
            new WindowsCapabilityProbe(),
            store,
            new ReviewCommands(store, new WindowsFrameMasker()),
            () => diagnostics(),
            _ =>
            {
                // Written down before the UI is told yes. Everything after this can fail — a file held
                // open by antivirus, or the process dying on the way out — and the next start will
                // finish the job rather than opening what survived (INV-12).
                LocalDataEraser.MarkPending(DataDirectory);
                _eraseOnShutdown = true;
                lifetime.StopApplication();
                return Task.FromResult(true);
            },
            indicators);
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
        // INV-4, at the one place a session can begin on its own. The tray icon and the pill live in the
        // UI process, so a service recording with nothing attached is a recording nobody was told about.
        var indicator = new IndicatorGuard(machine, () => indicators.Showing);
        indicator.Failed += failure => LogGuardFailed(logger, "indicator", failure.GetType().Name);

        var coordinator = new AutoSessionCoordinator(machine, policy, new SessionTrigger(policy), () => indicator.Indicated, logger);
        foreground.Changed += coordinator.Observe;

        await foreground.StartAsync(stoppingToken).ConfigureAwait(false);
        LogForegroundMode(logger, foreground.UsingHook ? "event hook" : "polling");
        var coordinating = coordinator.RunAsync(stoppingToken);

        // ST-024: hooks are installed while there is a session and not otherwise (ST-031). A WH_MOUSE_LL
        // hook makes every mouse move on the machine switch into this process, and the machine records
        // for a fraction of the day -- so for the rest of it that was a context switch per movement into
        // a process about to throw the answer away, paid as input latency in whatever the technician was
        // actually using (2026-09-20 review).
        //
        // Draining on a timer keeps the callbacks free of everything but a buffer write, as before, and
        // the state machine still decides whether what they see is recorded (INV-6).
        await using var hooks = new WindowsInputHooks();
        using var hookLifetime = new HookLifetime(machine, hooks.StartAsync, hooks.StopAsync);
        hookLifetime.Failed += failure => LogGuardFailed(logger, "input-hooks", failure.GetType().Name);
        hookLifetime.Changed += installed => LogHooks(logger, installed);
        hookLifetime.Follow(stoppingToken);

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
        var redaction = new RedactionWorker(store, recogniser, new WindowsFrameMasker(), redactionEngine, pending);
        redaction.BacklogChanged += machine.ReportPendingRedactions;

        // Without this the login heuristic fires into nothing: the frame is marked sensitive and the next
        // click still screenshots the same password prompt. INV-6 is about capture stopping, not about an
        // event being raised.
        var sensitive = new SensitiveContextGuard(machine);
        sensitive.Failed += failure => LogGuardFailed(logger, "sensitive-context", failure.GetType().Name);
        redaction.SensitiveContextSeen += sensitive.Seen;
        var showing = indicator.RunAsync(stoppingToken);
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
            (segment, ct) => machine.TryAppendTranscriptAsync(segment, ct),

            // The microphone is opened only while this is true, and what it hears while it is not is
            // dropped where it arrives (INV-6, INV-9). Both: the drop is the invariant, and not opening
            // the device is what keeps Windows' microphone-in-use indicator off a customer's screen for
            // the rest of the day (2026-09-20 review).
            recording: () => machine.State == SessionState.Recording);

        // The transition is the signal, for two things. The recorder would otherwise find out on its
        // next look and a session would begin with its first word already gone; and the model is not
        // loaded until there is a session to load it for.
        var firstSession = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        machine.StateChanged += state =>
        {
            narration.Nudge();
            if (state.State == CaptureStates.Recording)
            {
                _ = firstSession.TrySetResult();
            }
        };
        narration.Failed += failure => LogGuardFailed(logger, "narration", failure.GetType().Name);
        LogMicrophone(logger, microphone.DeviceName ?? "none", whisper.ModelName);

        // Nothing awaits this task, so anything it throws goes unobserved until shutdown. That is how an
        // egress refusal became a service with no narration and no explanation: PrepareAsync did not
        // catch it, this said nothing, and the fault sat in a task nobody was looking at. PrepareAsync
        // catches it now and answers with a reason; this is the line that prints the reason
        // (2026-09-20 review).
        //
        // Loaded when a session wants it, not when the service starts.
        //
        // ADR-0001 measured the model at 505 MB resident, which is most of ST-031's 600 MB for the whole
        // of ScreenTail — held all day on a machine that may record nothing at all that day. Waiting for
        // the first session costs that session the moment the model takes to load, which on a machine
        // that has already downloaded it is under a second and which AC3 already covers: audio heard
        // before the model is ready is dropped and counted, never queued (2026-09-20 review).
        var preparing = Task.Run(
            async () =>
            {
                try
                {
                    await firstSession.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The service stopped without ever recording anything, which on a quiet day is the
                    // ordinary outcome and the whole point of waiting. Returning rather than faulting,
                    // because this task is one of the set awaited at shutdown.
                    return;
                }

                if (microphone.DeviceName is not null && !await whisper.PrepareAsync(stoppingToken).ConfigureAwait(false))
                {
                    LogNoModel(logger, whisper.ModelName, whisper.Unavailable ?? "no reason given");
                }
            },
            stoppingToken);
        var listening = narration.RunAsync(stoppingToken);

        // Everything the "What's being captured right now?" panel shows now has a live source (ST-085).
        // Counts and states only: the scope decision names a process, never a window title (INV-10).
        diagnostics = () => Diagnostics(
            version,
            egress,
            egressPolicy,
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

        await Task.WhenAll(coordinating, capturing, sampling, redacting, showing, guarding, watchingFields, listening, preparing, draining).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Erased {Count} item(s) of local data; complete={Complete}")]
    private static partial void LogErased(ILogger logger, int count, bool complete);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Speech model {Model} could not be prepared ({Reason}); sessions will have no narration")]
    private static partial void LogNoModel(ILogger logger, string model, string reason);

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
        EgressPolicy egressPolicy,
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
            LocalOnly = egressPolicy.Settings.LocalOnly,
            PolicyVersion = "local", // ST-047 replaces this with the tenant's policy version.
            CpuPercent = 0,
            WorkingSetBytes = self.WorkingSet64,
            ServiceVersion = version,
        };
    }
}
