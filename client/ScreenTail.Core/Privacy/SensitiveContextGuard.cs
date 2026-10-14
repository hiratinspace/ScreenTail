using ScreenTail.Core.Audit;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <summary>
/// Turns "the redaction worker thinks that was a sign-in screen" into capture actually stopping (ST-041,
/// INV-6).
///
/// Without this the worker raises <see cref="RedactionWorker.SensitiveContextSeen"/> into nothing: the
/// heuristic fires, the frame is marked, and the next click still takes a screenshot of the same password
/// prompt. The acceptance criterion is about the product, not the class, so the event has to land somewhere.
///
/// One background loop owns the suppressed/recording decision and nothing else touches it. The obvious
/// alternative — start a timer on each sighting and unsuppress when it fires — has a race that resumes
/// capture while a login screen is still up: a second sighting arriving as the first timer expires can
/// leave the resume running after the re-suppress. A single loop comparing "should we be suppressed" with
/// "are we suppressed" cannot get into that state, and on a signal that is already seconds old (it comes
/// from OCR, not from the screen) a tick of latency costs nothing worth having a race for.
/// </summary>
public sealed class SensitiveContextGuard(SessionMachine machine, TimeProvider? time = null, TimeSpan? tick = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _tick = tick ?? TimeSpan.FromMilliseconds(250);
    private readonly Lock _gate = new();
    private long _failures;
    private DateTimeOffset _until;

    /// <summary>
    /// True while the session keeps this guard's hold, which a pause does not end. Asked of the session
    /// every time rather than remembered: a remembered hold went stale across a pause and across a
    /// restart, and was wrong in both directions (2026-10-12 review, T-41 and T-50).
    /// </summary>
    public bool Holding => machine.IsHeld(CaptureStateReason.SensitiveContext);

    /// <summary>How many separate times capture has been suppressed for this reason. Counts only (INV-10).</summary>
    public int Holds { get; private set; }

    /// <summary>
    /// Something that looked like a credential prompt was seen. Safe to call from the worker's callback:
    /// it takes a lock and returns. Repeat sightings extend the window rather than queueing another one,
    /// so a login screen that sits there keeps capture off for as long as it is there.
    /// </summary>
    public void Seen(TimeSpan window)
    {
        lock (_gate)
        {
            var until = _time.GetUtcNow() + window;
            if (until > _until)
            {
                _until = until;
            }
        }
    }

    /// <summary>
    /// One pass of the loop: bring the session into line with whether a sighting is still in its window.
    /// Separate from <see cref="RunAsync"/> so a test can step it against a clock it controls instead of
    /// sleeping and hoping.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        bool wanted;
        lock (_gate)
        {
            wanted = _until > _time.GetUtcNow();
        }

        // Asked of the session, not remembered. A session that stopped and started again inside the
        // ten-second window has no hold yet, and remembering one meant the credential prompt still on
        // screen was captured for the whole of the new session. A paused session keeps its hold, and
        // forgetting that meant the window ending was never passed on.
        var held = Holding;
        if (wanted == held)
        {
            return held;
        }

        if (wanted)
        {
            // Paused, the session records the hold and stays paused, so Resume goes straight to Suppressed.
            // Suppress fails when the session is stopped or idle, and correcting that is not this guard's
            // business. Then nothing is held, and the session says so.
            held = await machine.SuppressAsync(CaptureStateReason.SensitiveContext, ct).ConfigureAwait(false);
            if (held)
            {
                Holds++;
            }

            return held;
        }

        // The hold is over, paused or not. Paused, this leaves the session paused and lets go of the
        // hold, so Resume does not go back to a suppression nothing would lift (2026-10-12 review, T-41).
        _ = await machine.UnsuppressAsync(CaptureStateReason.SensitiveContext, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>How many passes ended in an exception. A rising count means the guard is struggling, not gone.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Told when a pass fails, so the host can log it. The type only, never the message (INV-10).</summary>
    public event Action<Exception>? Failed;

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tick, _time);
        await ResilientLoop.RunAsync(
            next: async token => await timer.WaitForNextTickAsync(token).ConfigureAwait(false),
            step: TickAsync,
            onFailure: failure =>
            {
                _ = Interlocked.Increment(ref _failures);
                Failed?.Invoke(failure);
            },
            ct).ConfigureAwait(false);
    }
}
