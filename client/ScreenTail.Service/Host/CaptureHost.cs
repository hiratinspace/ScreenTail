using System.Reflection;
using System.Runtime.Versioning;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Capture;
using ScreenTail.Service.Detection;
using ScreenTail.Service.Input;
using ScreenTail.Service.Ipc;
using ScreenTail.Service.Privacy;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>
/// The per-user capture service (ADR-0003): opens the encrypted store, recovers any session a crash left
/// behind (ST-020), publishes this run's IPC token, and serves the pipe with the state machine behind it.
/// Capture sources (hooks, screenshots, speech) and drafting arrive with their tickets.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class CaptureHost(ILogger<CaptureHost> logger) : BackgroundService
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var serviceExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the service executable.");

        await using var store = await SqliteSessionStore.OpenAsync(
            Path.Combine(DataDirectory, "store.db"),
            new DpapiKeyProvider(DpapiKeyProvider.DefaultKeyFilePath),
            ct: stoppingToken).ConfigureAwait(false);

        // Recover before serving, so the first UI to connect sees the outcome, not the orphan.
        await using var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        var recovered = await machine.RecoverAsync(stoppingToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            LogRecovered(logger, recovered);
        }

        var token = IpcToken.Generate();
        IpcTokenFile.Write(IpcTokenFile.DefaultPath, token);

        var verifier = new WindowsClientVerifier(serviceExecutable);
        var pipeName = WindowsPipeFactory.PipeNameForCurrentUser();
        await using var server = new IpcServer(
            WindowsPipeFactory.ForCurrentUser(pipeName),
            token,
            verifier,
            new CaptureController(machine, new WindowsCapabilityProbe()),
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

        var policy = new ScopePolicy(registry);
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

        // ST-041: without this, every staged frame stays redaction_pending and is deleted at finalize —
        // the session would end with no screenshots at all. This is also the only thing allowed to read a
        // pending frame or to clear the flag, so INV-1 rests on it.
        var recogniser = new WindowsOcrRecogniser();
        LogOcr(logger, recogniser.Available, recogniser.Language ?? "none");
        var redaction = new RedactionWorker(store, recogniser, new WindowsFrameMasker(), new RedactionEngine());
        redaction.BacklogChanged += machine.ReportPendingRedactions;

        // Without this the login heuristic fires into nothing: the frame is marked sensitive and the next
        // click still screenshots the same password prompt. INV-6 is about capture stopping, not about an
        // event being raised.
        var sensitive = new SensitiveContextGuard(machine);
        redaction.SensitiveContextSeen += sensitive.Seen;
        var guarding = sensitive.RunAsync(stoppingToken);

        var redacting = redaction.RunAsync(stoppingToken);

        // INV-12: retention runs at start and hourly. ST-047 feeds the tenant's retention days into the options.
        var retention = new RetentionJob(store, TimeProvider.System, new RetentionOptions(), () => machine.SessionId);
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

        await Task.WhenAll(coordinating, capturing, redacting, guarding).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capability {Capability} is {State}: {Message}")]
    private static partial void LogCapability(ILogger logger, string capability, string state, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} session(s) left behind by a previous run; marked partial and finalized")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service stopping")]
    private static partial void LogStopping(ILogger logger);
}
