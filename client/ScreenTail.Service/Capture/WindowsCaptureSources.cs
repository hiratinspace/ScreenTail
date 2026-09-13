using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Capture;

/// <summary>
/// What the state machine starts, stops, and asks for a frame (ST-029's mark moment).
///
/// The capture loops run for the life of the service and are gated by the machine's state rather than
/// started and stopped with each session, so <see cref="StartAsync"/> and <see cref="StopAsync"/> have
/// nothing to turn on — they clear the history that would otherwise carry from one session into the next.
/// A new session's first click should be captured, and its first sample should be a fresh baseline.
///
/// <see cref="MarkMomentAsync"/> is the one that does work. It is the only path that takes a frame because
/// the technician asked for one, so it goes around the click debouncer and the scene sampler entirely:
/// "this is the bit that matters" is never a duplicate of the frame before it, and never a scene that
/// looks too much like the last one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCaptureSources(
    IScreenshotCapturer capturer,
    Func<ScopeDecision?> currentScope,
    Action resetHistory,
    ILogger logger) : ICaptureSources
{
    private SessionMachine? _machine;

    public Task StartAsync(SessionMachine machine, CancellationToken ct = default)
    {
        _machine = machine;
        resetHistory();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _machine = null;
        resetHistory();
        return Task.CompletedTask;
    }

    public async Task MarkMomentAsync(CancellationToken ct = default)
    {
        if (_machine is not { } machine)
        {
            return;
        }

        // INV-5 still applies. A technician can press the chord over anything, and scope decides what may
        // be photographed — asking for a frame is not permission to take one of a customer's inbox.
        var scope = currentScope();
        if (scope is null || !scope.MayCaptureFrames)
        {
            LogOutOfScope(logger);
            return;
        }

        var frame = capturer.CaptureForegroundWindow(expected: scope.Window);
        if (frame is null)
        {
            LogNoFrame(logger);
            return;
        }

        // The marker event is written by the machine after this returns, so the frame is in the store
        // first and the marker never points at a session moment with nothing behind it.
        _ = await machine.TryStageFrameAsync(
            new StagedFrame(
                Guid.NewGuid().ToString("N")[..12],
                machine.NowMs,
                FrameTrigger.Marker,
                frame.Width,
                frame.Height,
                null,
                frame.Image),
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mark moment: the window in front is out of scope, so no frame was taken")]
    private static partial void LogOutOfScope(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mark moment: the window was gone before it could be captured")]
    private static partial void LogNoFrame(ILogger logger);
}
