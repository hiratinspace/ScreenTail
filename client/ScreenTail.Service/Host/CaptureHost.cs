using System.Reflection;
using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Store;
using ScreenTail.Service.Ipc;
using ScreenTail.Service.Store;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>
/// The per-user capture service (ADR-0003): opens the encrypted store, publishes this run's IPC token,
/// and serves the pipe. Capture itself arrives with ST-020 onward; until then the controller is a placeholder.
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
            stoppingToken).ConfigureAwait(false);

        var token = IpcToken.Generate();
        IpcTokenFile.Write(IpcTokenFile.DefaultPath, token);

        var verifier = new WindowsClientVerifier(serviceExecutable);
        var pipeName = WindowsPipeFactory.PipeNameForCurrentUser();
        await using var server = new IpcServer(
            WindowsPipeFactory.ForCurrentUser(pipeName),
            token,
            verifier,
            new PlaceholderCaptureController(),
            store,
            version);
        Array.Clear(token);
        server.Start();

        LogStarted(logger, IpcContract.Version, verifier.ServiceIsSigned ? "signed-publisher" : "dev-same-directory");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        LogStopping(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service started: IPC contract v{IpcVersion}, client verification {Mode}")]
    private static partial void LogStarted(ILogger logger, int ipcVersion, string mode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service stopping")]
    private static partial void LogStopping(ILogger logger);
}
