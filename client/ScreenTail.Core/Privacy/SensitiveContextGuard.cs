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

    /// <summary>True while this guard is holding capture suppressed.</summary>
    public bool Holding { get; private set; }

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

        // Re-derived from the session rather than trusted, because Holding is only this guard's belief.
        // A session that stopped and started again inside the ten-second window left it true while the
        // new session was Recording: the guard saw "already holding", did nothing, and the credential
        // prompt still on screen was captured for the whole of the new session.
        if (Holding && machine.State is not SessionState.Suppressed)
        {
            Holding = false;
        }

        if (wanted == Holding)
        {
            return Holding;
        }

        if (wanted)
        {
            // Suppress fails when the session is paused, stopped, or not running, and correcting that is
            // not this guard's business. But then it is holding nothing and has to say so, or the next
            // tick would "resume" a session it never suppressed.
            Holding = await machine.SuppressAsync(CaptureStateReason.SensitiveContext, ct).ConfigureAwait(false);
            if (Holding)
            {
                Holds++;
            }
        }
        else
        {
            // Whether or not this moved the session — the technician may have paused it meanwhile, which
            // leaves Unsuppress a no-op — the hold is over.
            _ = await machine.UnsuppressAsync(ct).ConfigureAwait(false);
            Holding = false;
        }

        return Holding;
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
