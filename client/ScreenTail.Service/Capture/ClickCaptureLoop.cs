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
/// Drains the hook buffer and turns what it finds into timeline events and screenshots (ST-025).
///
/// This is where the capture rules meet: the state machine decides whether anything may be recorded at all
/// (INV-6), scope decides whether this particular window may be photographed (INV-5), and the debouncer
/// decides whether this click is a new action or part of one already captured. A click that fails the scope
/// test still becomes an event — the timeline stays honest about where the technician went — but no frame
/// is taken.
///
/// Frames are staged with <c>redaction_pending</c>, which is the only state a frame can be born in: nothing
/// outside the redaction worker can read one until it has been through ST-041 (INV-1).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ClickCaptureLoop(
    SessionMachine machine,
    WindowsInputHooksAccessor hooks,
    IScreenshotCapturer capturer,
    Func<ScopeDecision?> currentScope,
    ILogger logger)
{
    private readonly InputSignalReader _reader = new();
    private readonly ClickDebouncer _debouncer = new();
    private readonly InputSignal[] _scratch = new InputSignal[2048];

    /// <summary>A new session starts with no history, so its first click is captured (ST-029).</summary>
    public void Reset() => _debouncer.Reset();

    public async Task RunAsync(CancellationToken ct)
    {
        // Drained on a timer rather than on every signal: the callbacks must stay free of everything but a
        // buffer write, and a click is worth a frame a few tens of milliseconds later just as much.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await DrainAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var count = hooks.Buffer.Drain(_scratch);
        if (count == 0)
        {
            return;
        }

        if (machine.State != SessionState.Recording)
        {
            // Paused, suppressed or idle: the signals are dropped rather than held, so nothing observed
            // while capture was off can arrive late and land in the session (INV-6).
            return;
        }

        var sessionStart = machine.SessionStartedAt;
        var events = _reader.Read(
            _scratch.AsMemory(0, count),
            timestamp => WindowsInputHooks.ToSessionMs(timestamp, sessionStart),
            WindowsInputHooks.Elapsed);

        foreach (var sessionEvent in events)
        {
            await machine.TryRecordEventAsync(sessionEvent, ct).ConfigureAwait(false);
            if (sessionEvent is ClickEvent click)
            {
                await CaptureForAsync(click, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CaptureForAsync(ClickEvent click, CancellationToken ct)
    {
        var scope = currentScope();
        if (scope is null || !scope.MayCaptureFrames)
        {
            return;
        }

        if (!_debouncer.ShouldCapture())
        {
            return;
        }

        var frame = capturer.CaptureForegroundWindow();
        if (frame is null)
        {
            // The window closed between the click and the capture. A missing frame, never a blank one.
            LogNoFrame(logger, click.TsMs);
            return;
        }

        await machine.TryStageFrameAsync(
            new StagedFrame(
                Guid.NewGuid().ToString("N")[..12],
                click.TsMs,
                FrameTrigger.Click,
                frame.Width,
                frame.Height,
                new Point { X = click.X, Y = click.Y },
                frame.Image),
            ct).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var timing = frame.Timing.ToString();
            var bytes = frame.Image.Length;
            LogFrame(logger, frame.SourceWidth, frame.SourceHeight, frame.Width, frame.Height, bytes, timing);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Staged a frame: {SourceWidth}x{SourceHeight} captured, stored {Width}x{Height} in {Bytes} bytes ({Timing})")]
    private static partial void LogFrame(ILogger logger, int sourceWidth, int sourceHeight, int width, int height, int bytes, string timing);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No frame for the click at {TsMs} ms: the window was gone")]
    private static partial void LogNoFrame(ILogger logger, long tsMs);
}

/// <summary>Lets the capture loop read the hook buffer without owning the hooks' lifetime.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputHooksAccessor(WindowsInputHooks hooks)
{
    public InputRingBuffer Buffer => hooks.Buffer;
}
