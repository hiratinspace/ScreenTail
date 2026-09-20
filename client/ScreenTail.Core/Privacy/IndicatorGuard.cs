using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <summary>
/// Stops capture while nothing is on screen to say it is happening (INV-4).
///
/// The tray icon and the pill live in the UI process, and the service records without them: it starts
/// sessions on its own when a remote tool takes the foreground, it registers the global hotkeys itself,
/// and nothing anywhere asked whether a UI was attached. So a UI that crashed, was quit from its own
/// menu, or was never started at all left the service recording a customer's screen with no indicator of
/// any kind — which is the one thing INV-4 exists to forbid, arrived at without touching capture
/// (2026-09-19 review).
///
/// <b>Suppression rather than stopping.</b> INV-6 already means a suppressed session writes nothing —
/// no frame, no typing count, no transcript — so the invariant holds the moment this takes effect. It
/// also means a technician whose UI crashed mid-job gets the session back when it reopens, instead of
/// one job cut into two notes.
///
/// The grace period is the whole subtlety. The UI reconnects a moment after a restart, and suppressing
/// on every blink would fill the timeline with intervals that mean nothing.
/// </summary>
public sealed class IndicatorGuard(
    SessionMachine machine,
    Func<int> attachedUis,
    TimeProvider? time = null,
    IndicatorOptions? options = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IndicatorOptions _options = options ?? new IndicatorOptions();
    private DateTimeOffset? _goneSince;
    private long _failures;

    /// <summary>True while this guard is holding capture suppressed.</summary>
    public bool Holding { get; private set; }

    /// <summary>How many separate times capture has been suppressed for this. Counts only (INV-10).</summary>
    public int Holds { get; private set; }

    /// <summary>How many passes ended in an exception. A rising count means it is struggling, not gone.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Told when a pass fails, so the host can log it. The type only, never the message.</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// Whether capture may run right now.
    ///
    /// Asked before a session is started as well as during one, because a session that should never have
    /// begun is worse than one that is suppressed a moment later: the technician sees nothing either way,
    /// and the first has a start time in the timeline that nobody witnessed.
    /// </summary>
    public bool Indicated => attachedUis() > 0;

    /// <summary>
    /// One pass: bring the session into line with whether anything is showing the indicator.
    ///
    /// Separate from <see cref="RunAsync"/> so a test can step it against a clock it controls.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (Indicated)
        {
            _goneSince = null;
        }
        else
        {
            _goneSince ??= now;
        }

        // Re-derived from the session rather than trusted: Holding is only this guard's belief, and the
        // other guards share the same suppressed state. One of them lifting it would otherwise leave
        // this one thinking it still held something (the bug PasswordFieldGuard had until 2026-09-20).
        if (Holding && machine.State is not SessionState.Suppressed)
        {
            Holding = false;
        }

        var wanted = _goneSince is { } since && now - since >= _options.Grace;
        if (wanted == Holding)
        {
            return Holding;
        }

        if (wanted)
        {
            // Suppress fails when the session is paused, stopped or idle, and correcting that is not this
            // guard's business. But then it holds nothing and must say so, or the next tick would
            // "resume" a session it never suppressed.
            Holding = await machine.SuppressAsync(CaptureStateReason.NoIndicator, ct).ConfigureAwait(false);
            if (Holding)
            {
                Holds++;
            }
        }
        else
        {
            _ = await machine.UnsuppressAsync(ct).ConfigureAwait(false);
            Holding = false;
        }

        return Holding;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.Tick, _time);
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

public sealed record IndicatorOptions
{
    /// <summary>
    /// How long the indicator may be absent before capture stops.
    ///
    /// The UI reconnects a moment after its own restart, and suppressing on every blink would fill the
    /// timeline with intervals that mean nothing and interrupt a session nobody interrupted. Three
    /// seconds is longer than a reconnect and far shorter than anything a person would fail to notice.
    /// </summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(500);
}
