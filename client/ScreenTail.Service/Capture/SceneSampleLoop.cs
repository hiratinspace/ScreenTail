using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;

namespace ScreenTail.Service.Capture;

/// <summary>
/// Looks at the screen once a second and stages a frame when it has changed (ST-026).
///
/// ST-025 photographs clicks, which misses everything a technician reads rather than does: an error dialog
/// appearing while they watch, a progress bar finishing, a service finally starting. Those are often the
/// screenshot the ticket note needs, and no click marks them.
///
/// Windows-only on purpose, and deliberately thin: this owns the timer, keeps the loop alive through a bad
/// sample, and logs. Every decision about whether a sample may be taken, and whether it becomes a frame,
/// lives in <see cref="SceneRecorder"/> in Core, where a test can reach it without a Windows machine. The
/// scope gate used to sit here, where deleting it kept the suite green (2026-10-12 review, T-10).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SceneSampleLoop
{
    private readonly SceneRecorder _recorder;
    private readonly SessionMachine _machine;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <param name="time">The clock the sampling timer runs on. The system's when null.</param>
    public SceneSampleLoop(
        SessionMachine machine,
        IScreenshotCapturer capturer,
        Func<ScopeDecision?> currentScope,
        ILogger logger,
        SceneSampler? sampler = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _machine = machine;
        _logger = logger;
        _recorder = new SceneRecorder(machine, capturer, currentScope, sampler);
        _recorder.FrameMissed += () => LogNoFrame(logger);
        _recorder.FrameStaged += _ =>
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var counts = _recorder.Counts;
                LogScene(logger, counts.Kept, counts.Seen, counts.Unchanged, counts.OverBudget);
            }
        };
    }

    public SceneCounts Counts => _recorder.Counts;

    /// <summary>A new session starts with no baseline, so nothing is compared across the gap (ST-029).</summary>
    public void Reset() => _recorder.Reset();

    public async Task RunAsync(CancellationToken ct)
    {
        // Only while a session is under way (ST-031): with none, every sample found the machine not
        // recording and returned, once a second all day. The period is read once, here, as it always was,
        // so a different sampling interval takes effect when the service next starts.
        //
        // One sample that cannot be taken or stored costs that sample. This loop used to end on the first
        // exception of any kind, and a session then went on with clicks and no scene frames at all.
        await SessionLoop.RunAsync(
            _machine,
            _recorder.Options.SampleEvery,
            _recorder.SampleAsync,
            failure => LogSampleFailed(_logger, failure.GetType().Name),
            ct,
            _time).ConfigureAwait(false);
    }

    // The type and never the message: a store error can quote what it was asked to write (INV-10).
    [LoggerMessage(Level = LogLevel.Warning, Message = "A scene sample hit {Error}; sampling carries on")]
    private static partial void LogSampleFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scene frame {Kept} of {Seen} samples ({Unchanged} unchanged, {OverBudget} over budget)")]
    private static partial void LogScene(ILogger logger, long kept, long seen, long unchanged, long overBudget);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The screen changed but the window was gone before it could be captured")]
    private static partial void LogNoFrame(ILogger logger);
}
