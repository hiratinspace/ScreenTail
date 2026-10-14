using ScreenTail.Core.Detection;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Capture;

/// <summary>
/// What the state machine starts, stops, and asks for a frame (ST-029's mark moment).
///
/// The capture loops run for the life of the service and are gated by the machine's state rather than
/// started and stopped with each session, so <see cref="StartAsync"/> and <see cref="StopAsync"/> have
/// nothing to turn on: they clear the history that would otherwise carry from one session into the next.
/// A new session's first click should be captured, and its first sample should be a fresh baseline.
///
/// <see cref="MarkMomentAsync"/> is the one that does work. It is the only path that takes a frame because
/// the technician asked for one, so it goes around the click debouncer and the scene sampler entirely:
/// "this is the bit that matters" is never a duplicate of the frame before it, and never a scene that
/// looks too much like the last one. It does not go around scope.
///
/// In Core so that a test can reach the scope gate. In the Windows service no test referenced it, and
/// deleting it left the suite green (2026-10-12 review, T-10).
/// </summary>
public sealed class ScopedCaptureSources(
    IScreenshotCapturer capturer,
    Func<ScopeDecision?> currentScope,
    Action resetHistory) : ICaptureSources
{
    private SessionMachine? _machine;

    /// <summary>Raised when a moment was marked over a window that may not be photographed. The marker is still written.</summary>
    public event Action? MomentOutOfScope;

    /// <summary>
    /// Raised when scope allowed frames but named no window to prove the picture against, so none was taken.
    /// Kept apart from <see cref="MomentOutOfScope"/> so a log never blames scope for it. The marker is still written.
    /// </summary>
    public event Action? MomentWithNoWindow;

    /// <summary>Raised when the window was gone before the picture was taken.</summary>
    public event Action? MomentMissed;

    public Task StartAsync(SessionMachine machine, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        Volatile.Write(ref _machine, machine);
        resetHistory();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Volatile.Write(ref _machine, null);
        resetHistory();
        return Task.CompletedTask;
    }

    public async Task MarkMomentAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _machine) is not { } machine)
        {
            return;
        }

        // INV-5 still applies. A technician can press the chord over anything, and scope decides what may
        // be photographed: asking for a frame is not permission to take one of a customer's inbox.
        var scope = currentScope();
        if (FrameGate.WindowFor(scope) is not { } window)
        {
            (scope is { MayCaptureFrames: true } ? MomentWithNoWindow : MomentOutOfScope)?.Invoke();
            return;
        }

        var frame = capturer.CaptureForegroundWindow(window);
        if (frame is null)
        {
            MomentMissed?.Invoke();
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
}
