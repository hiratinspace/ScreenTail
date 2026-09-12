using ScreenTail.Shared.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>Placeholder for the per-user capture service host; ST-004 adds IPC, ST-020 the session lifecycle.</summary>
internal sealed partial class CaptureHost(ILogger<CaptureHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, IpcContract.Version);
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture service started (IPC contract v{IpcVersion})")]
    private static partial void LogStarted(ILogger logger, int ipcVersion);
}
