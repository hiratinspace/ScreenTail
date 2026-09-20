using System.Runtime.Versioning;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Capture;

/// <summary>
/// Looks at the screen once a second and stages a frame when it has changed (ST-026).
///
/// ST-025 photographs clicks, which misses everything a technician reads rather than does: an error dialog
/// appearing while they watch, a progress bar finishing, a service finally starting. Those are often the
/// screenshot the ticket note needs, and no click marks them.
///
/// The same two gates as the click loop, for the same reasons: the state machine decides whether anything
/// may be recorded (INV-6) and scope decides whether this window may be photographed (INV-5). A sample is
/// taken only when both allow it — not taken and then discarded — because the cheapest way to honour INV-5
/// is to never point the camera at an out-of-scope window in the first place.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SceneSampleLoop(
    SessionMachine machine,
    IScreenshotCapturer capturer,
    Func<ScopeDecision?> currentScope,
    ILogger logger,
    SceneSampler? sampler = null)
{
    private readonly SceneSampler _sampler = sampler ?? new SceneSampler();

    public SceneCounts Counts => _sampler.Counts;

    /// <summary>A new session starts with no baseline, so nothing is compared across the gap (ST-029).</summary>
    public void Reset() => _sampler.Reset();

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_sampler.Options.SampleEvery);
        var recording = false;

        // One sample that cannot be taken or stored costs that sample. This loop used to end on the first
        // exception of any kind, and a session then went on with clicks and no scene frames at all.
        await ResilientLoop.RunAsync(
            next: async token => await timer.WaitForNextTickAsync(token).ConfigureAwait(false),
            step: async token =>
            {
                if (machine.State != SessionState.Recording)
                {
                    // A session that stops and starts again is a new scene: the screen almost certainly
                    // moved on while capture was off, and comparing against what was there before the pause
                    // would either miss the change or report a stale one.
                    if (recording)
                    {
                        _sampler.Reset();
                        recording = false;
                    }

                    return;
                }

                recording = true;
                await SampleAsync(token).ConfigureAwait(false);
            },
            onFailure: failure => LogSampleFailed(logger, failure.GetType().Name),
            ct).ConfigureAwait(false);
    }

    // The type and never the message: a store error can quote what it was asked to write (INV-10).
    [LoggerMessage(Level = LogLevel.Warning, Message = "A scene sample hit {Error}; sampling carries on")]
    private static partial void LogSampleFailed(ILogger logger, string error);

    private async Task SampleAsync(CancellationToken ct)
    {
        var scope = currentScope();
        if (scope is null || !scope.MayCaptureFrames)
        {
            return;
        }

        var grid = capturer.CaptureSceneGrid(scope.Window);
        if (grid is null)
        {
            return;
        }

        if (_sampler.Offer(PerceptualHash.OfGrid(grid)) != SceneDecision.Keep)
        {
            return;
        }

        // The screen changed, so now it is worth the full capture. Everything before this point cost one
        // StretchBlt into a 17x16 bitmap.
        var frame = capturer.CaptureForegroundWindow(expected: scope.Window);
        if (frame is null)
        {
            LogNoFrame(logger);
            return;
        }

        await machine.TryStageFrameAsync(
            new StagedFrame(
                Guid.NewGuid().ToString("N")[..12],
                machine.NowMs,
                FrameTrigger.SceneChange,
                frame.Width,
                frame.Height,
                null,
                frame.Image),
            ct).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var counts = _sampler.Counts;
            LogScene(logger, counts.Kept, counts.Seen, counts.Unchanged, counts.OverBudget);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scene frame {Kept} of {Seen} samples ({Unchanged} unchanged, {OverBudget} over budget)")]
    private static partial void LogScene(ILogger logger, long kept, long seen, long unchanged, long overBudget);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The screen changed but the window was gone before it could be captured")]
    private static partial void LogNoFrame(ILogger logger);
}
