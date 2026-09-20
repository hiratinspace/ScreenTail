using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Input;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Input;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Capture;

/// <summary>
/// Drains the hook buffer and hands what it finds to the recorder (ST-025).
///
/// Windows-only on purpose, and deliberately thin: this reads the ring buffer the hook callbacks write
/// into, hands the raw signals on, and logs what a frame cost. Every decision about what
/// may be recorded or photographed lives in <see cref="SessionRecorder"/> in Core, where a test can reach
/// it without a Windows machine — which is what INV-5 and INV-6 turn on, and what this file used to hide
/// (ST-048, weaknesses P0-4).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ClickCaptureLoop
{
    private readonly InputSignal[] _scratch = new InputSignal[2048];
    private readonly SessionMachine _machine;
    private readonly WindowsInputHooksAccessor _hooks;
    private readonly SessionRecorder _recorder;
    private readonly ILogger _logger;
    private long _failedDrains;

    public ClickCaptureLoop(
        SessionMachine machine,
        WindowsInputHooksAccessor hooks,
        IScreenshotCapturer capturer,
        Func<ScopeDecision?> currentScope,
        ILogger logger)
    {
        _machine = machine;
        _hooks = hooks;
        _logger = logger;
        _recorder = new SessionRecorder(machine, capturer, currentScope);
        _recorder.FrameMissed += tsMs => LogNoFrame(logger, tsMs);
        _recorder.FrameStaged += frame =>
        {
            // Guarded because the timing string is built to be logged and is wasted otherwise.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var timing = frame.Timing.ToString();
                LogFrame(logger, frame.SourceWidth, frame.SourceHeight, frame.Width, frame.Height, frame.Image.Length, timing);
            }
        };
    }

    /// <summary>Keyboard events dropped because the window was out of scope (INV-6). Counts only.</summary>
    public long DroppedOutOfScope => _recorder.DroppedOutOfScope;

    /// <summary>A new session starts with no history, so its first click is captured (ST-029).</summary>
    public void Reset() => _recorder.Reset();

    public async Task RunAsync(CancellationToken ct)
    {
        // Drained on a timer rather than on every signal: the callbacks must stay free of everything but a
        // buffer write, and a click is worth a frame a few tens of milliseconds later just as much.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await DrainAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad batch costs that batch. This loop used to end on the first exception of any
                    // kind and stay ended for the life of the service, with the pill still saying
                    // Recording and no click ever captured again. The type and a count, never the
                    // message: a store error can quote what it was asked to write (INV-10).
                    _failedDrains++;
                    LogDrainFailed(_logger, ex.GetType().Name, _failedDrains);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var count = _hooks.Buffer.Drain(_scratch);
        if (count == 0)
        {
            return;
        }

        // Raw signals, not finished events. What may be counted is decided in Core before the keys are
        // added up; turning them into a burst here first is how a password's length got into a session.
        var sessionStart = _machine.SessionStartedAt;
        await _recorder.RecordSignalsAsync(
            _scratch.AsMemory(0, count),
            timestamp => WindowsInputHooks.ToSessionMs(timestamp, sessionStart),
            WindowsInputHooks.Elapsed,
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Staged a frame: {SourceWidth}x{SourceHeight} captured, stored {Width}x{Height} in {Bytes} bytes ({Timing})")]
    private static partial void LogFrame(ILogger logger, int sourceWidth, int sourceHeight, int width, int height, int bytes, string timing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An input batch could not be recorded ({Error}); {Total} so far. Capture continues.")]
    private static partial void LogDrainFailed(ILogger logger, string error, long total);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No frame for the click at {TsMs} ms: the window was gone")]
    private static partial void LogNoFrame(ILogger logger, long tsMs);
}

/// <summary>Lets the capture loop read the hook buffer without owning the hooks' lifetime.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputHooksAccessor(WindowsInputHooks hooks)
{
    public InputRingBuffer Buffer => hooks.Buffer;
}
