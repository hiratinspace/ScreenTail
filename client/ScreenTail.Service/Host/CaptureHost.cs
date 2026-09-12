using System.Reflection;
using System.Runtime.Versioning;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Ipc;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>
/// The per-user capture service (ADR-0003): opens the encrypted store, recovers any session a crash left
/// behind (ST-020), publishes this run's IPC token, and serves the pipe with the state machine behind it.
/// Capture sources (hooks, screenshots, speech) and drafting arrive with their tickets.
/// </summary>
[SupportedOSPlatform("windows")]
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

        LogStopping(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention removed raw data from {Count} session(s)")]
    private static partial void LogRetention(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service started: IPC contract v{IpcVersion}, client verification {Mode}, state {State}")]
    private static partial void LogStarted(ILogger logger, int ipcVersion, string mode, string state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capability {Capability} is {State}: {Message}")]
    private static partial void LogCapability(ILogger logger, string capability, string state, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} session(s) left behind by a previous run; marked partial and finalized")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service stopping")]
    private static partial void LogStopping(ILogger logger);
}
