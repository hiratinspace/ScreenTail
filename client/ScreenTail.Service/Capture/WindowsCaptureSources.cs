using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Sessions;

namespace ScreenTail.Service.Capture;

/// <summary>
/// What the state machine starts, stops, and asks for a frame (ST-029's mark moment), with the service's
/// logging attached.
///
/// Deliberately thin. The rules, including the scope gate that keeps "mark moment" from photographing an
/// excluded window, live in <see cref="ScopedCaptureSources"/> in Core, where a test can reach them. They
/// used to sit here, where deleting the gate kept the suite green (2026-10-12 review, T-10).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCaptureSources : ICaptureSources
{
    private readonly ScopedCaptureSources _sources;

    public WindowsCaptureSources(
        IScreenshotCapturer capturer,
        Func<ScopeDecision?> currentScope,
        Action resetHistory,
        ILogger logger)
    {
        _sources = new ScopedCaptureSources(capturer, currentScope, resetHistory);
        _sources.MomentOutOfScope += () => LogOutOfScope(logger);
        _sources.MomentMissed += () => LogNoFrame(logger);
    }

    public Task StartAsync(SessionMachine machine, CancellationToken ct = default) => _sources.StartAsync(machine, ct);

    public Task StopAsync(CancellationToken ct = default) => _sources.StopAsync(ct);

    public Task MarkMomentAsync(CancellationToken ct = default) => _sources.MarkMomentAsync(ct);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mark moment: the window in front is out of scope, so no frame was taken")]
    private static partial void LogOutOfScope(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mark moment: the window was gone before it could be captured")]
    private static partial void LogNoFrame(ILogger logger);
}
