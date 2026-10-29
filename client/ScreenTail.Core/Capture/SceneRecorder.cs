using ScreenTail.Core.Detection;
using ScreenTail.Core.Perf;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Capture;

/// <summary>
/// Turns one look at the screen into a scene frame, under the capture rules (ST-026).
///
/// The same two gates as the click path, for the same reasons: the state machine decides whether anything
/// may be recorded (INV-6) and scope decides whether this window may be photographed (INV-5). A sample is
/// taken only when both allow it, not taken and then discarded, because the cheapest way to honour INV-5
/// is to never point the camera at an out-of-scope window in the first place.
///
/// It lives in Core, beside <see cref="SessionRecorder"/>, for the reason that one does. While these gates
/// sat in the Windows sampling loop no test project referenced them, and deleting either one left the
/// whole suite green while the service photographed an excluded window (2026-10-12 review, T-10). The
/// timer and the screen grab are still Windows-only; the decisions are here.
/// </summary>
public sealed class SceneRecorder(
    SessionMachine machine,
    IScreenshotCapturer capturer,
    Func<ScopeDecision?> currentScope,
    SceneSampler? sampler = null,
    CaptureThrottle? throttle = null)
{
    private readonly SceneSampler _sampler = sampler ?? new SceneSampler();
    private int _resetRequested;
    private bool _recording;
    private int _ticksSinceLook;

    /// <summary>How often the screen is looked at, among the rest of the technician's sensitivity.</summary>
    public SceneSamplerOptions Options => _sampler.Options;

    public SceneCounts Counts => _sampler.Counts;

    /// <summary>Raised after the machine accepts a scene frame, so the host can log what it cost. Never carries pixels to a log.</summary>
    public event Action<CapturedFrame>? FrameStaged;

    /// <summary>Raised when the screen changed but the window was gone before the picture was taken.</summary>
    public event Action? FrameMissed;

    /// <summary>
    /// A new session starts with no baseline, so nothing is compared across the gap (ST-029).
    ///
    /// Asked for here and done by the next sample. A session starts and stops on whichever thread sent the
    /// command, while samples are taken on the sampling loop's, and the sampler's history is not built to
    /// be cleared under a sample that is reading it.
    /// </summary>
    public void Reset() => Volatile.Write(ref _resetRequested, 1);

    /// <summary>One look at the screen. Called once per tick by a single loop, never concurrently.</summary>
    public async Task SampleAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
        {
            _sampler.Reset();
        }

        if (machine.State != SessionState.Recording)
        {
            // A session that stops and starts again is a new scene: the screen almost certainly moved on
            // while capture was off, and comparing against what was there before the pause would either
            // miss the change or report a stale one.
            if (_recording)
            {
                _sampler.Reset();
                _recording = false;
            }

            return;
        }

        _recording = true;

        // ST-031: over the processor budget, the screen is looked at on one tick in two, then one in four.
        // The first thing to give, and the cheapest to give safely: a look skipped is a picture never
        // taken, so nothing about how a taken frame is redacted changes (INV-1). After the state check,
        // so a skipped tick still sees a session end and forgets the baseline.
        if (throttle is not null && ++_ticksSinceLook < throttle.SampleEveryNth)
        {
            return;
        }

        _ticksSinceLook = 0;
        if (FrameGate.WindowFor(currentScope()) is not { } window)
        {
            return;
        }

        var grid = capturer.CaptureSceneGrid(window);
        if (grid is null)
        {
            return;
        }

        if (_sampler.Offer(PerceptualHash.OfGrid(grid)) != SceneDecision.Keep)
        {
            return;
        }

        // The screen changed, so now it is worth the full capture. Everything before this point cost one
        // StretchBlt into a 17x16 bitmap. Against the same window the grid was taken of: if the technician
        // moved to another one in between, the capturer finds it is not the one judged and takes nothing.
        var frame = capturer.CaptureForegroundWindow(window);
        if (frame is null)
        {
            FrameMissed?.Invoke();
            return;
        }

        // Staged with redaction pending, the only state a frame can be born in (INV-1).
        var staged = await machine.TryStageFrameAsync(
            new StagedFrame(
                Guid.NewGuid().ToString("N")[..12],
                machine.NowMs,
                FrameTrigger.SceneChange,
                frame.Width,
                frame.Height,
                null,
                frame.Image),
            ct).ConfigureAwait(false);

        if (staged)
        {
            FrameStaged?.Invoke(frame);
        }
    }
}
