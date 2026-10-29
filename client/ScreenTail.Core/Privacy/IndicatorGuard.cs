using ScreenTail.Core.Ipc;
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
///
/// <b>The pill put away counts between sessions only.</b> Between sessions the technician may hide the
/// pill, and the UI then says so with a zero-size report, which lets the next session start (owner
/// decision, 2026-10-25). Once a session runs, only the pill counts. The pill has the same grace a UI
/// restart has to come back, counted from the moment the session started rather than from the report's
/// time or this guard's first look, and after that capture is suppressed as for a missing pill. The
/// zero-size report used to go on counting for its whole lifetime, plus the grace after it, and for as
/// long as a UI that had missed the start went on sending it.
/// </summary>
/// <param name="showing">Whether any indicator report is current, the pill put away included.</param>
/// <param name="pill">
/// Whether a current report has the pill's own rectangle. What a running session needs. Null for a caller
/// whose reports are all of the pill, so the two read the same.
/// </param>
public sealed class IndicatorGuard(
    SessionMachine machine,
    Func<bool> showing,
    TimeProvider? time = null,
    IndicatorOptions? options = null,
    Func<bool>? pill = null)
{
    /// <summary>
    /// The guard over the UI's reports, as the service wires it: any report counts between sessions, and
    /// only the pill during one.
    /// </summary>
    public IndicatorGuard(SessionMachine machine, IndicatorReports reports, TimeProvider? time = null, IndicatorOptions? options = null)
        : this(machine, () => reports.Showing, time, options, () => reports.PillShowing)
    {
    }

    private readonly Func<bool> _pill = pill ?? showing;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IndicatorOptions _options = options ?? new IndicatorOptions();
    private DateTimeOffset? _goneSince;
    private long _failures;

    /// <summary>
    /// True while the session keeps this guard's hold, which a pause does not end. Asked of the session
    /// every time rather than remembered: a remembered hold went stale across a pause and across a
    /// restart, and was wrong in both directions (2026-10-12 review, T-41 and T-50).
    /// </summary>
    public bool Holding => machine.IsHeld(CaptureStateReason.NoIndicator);

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
    ///
    /// <b>This used to count connections.</b> A connection is not a pill: the window could be on a
    /// monitor that had since been unplugged, the tray icon could be in the overflow flyout, and the UI
    /// could have frozen while still holding the socket — and all three read as indicated. It is a
    /// report now, repeated while it is true and expiring when it stops (2026-09-20 review).
    ///
    /// The service cannot verify the report, and a process that would lie about it could just as well
    /// connect and say nothing, which is what used to be enough. This catches the accidents, not a
    /// hostile client.
    ///
    /// Between sessions any report counts, the pill put away included, so every start has the same gate:
    /// automatic, the hotkey and the pipe. While a session runs only the pill counts.
    /// </summary>
    public bool Indicated => UnderWay(machine.State) ? _pill() : showing();

    /// <summary>
    /// One pass: bring the session into line with whether anything is showing the indicator.
    ///
    /// Separate from <see cref="RunAsync"/> so a test can step it against a clock it controls.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var underWay = UnderWay(machine.State);
        if (underWay ? _pill() : showing())
        {
            _goneSince = null;
        }
        else
        {
            // Only the pill put away, in a session: the pill has been missing since the session started,
            // so its grace runs from then, and the session's own clock says how long ago that was. With
            // nothing reported at all, the grace runs from the first pass that noticed, as for a restart.
            var missing = underWay && showing() ? now - TimeSpan.FromMilliseconds(machine.NowMs) : now;
            _goneSince = _goneSince is { } gone && gone <= missing ? gone : missing;
        }

        // Asked of the session, not remembered: the session keeps one hold per guard, a pause keeps
        // them, and a session ending drops them.
        var held = Holding;
        var wanted = _goneSince is { } since && now - since >= _options.Grace;
        if (wanted == held)
        {
            return held;
        }

        if (wanted)
        {
            // Paused, the session records the hold and stays paused, so Resume goes straight to Suppressed.
            // Suppress fails when the session is stopped or idle, and correcting that is not this guard's
            // business. Then nothing is held, and the session says so.
            held = await machine.SuppressAsync(CaptureStateReason.NoIndicator, ct).ConfigureAwait(false);
            if (held)
            {
                Holds++;
            }

            return held;
        }

        // The hold is over, paused or not. Paused, this leaves the session paused and lets go of the
        // hold, so Resume does not go back to a suppression nothing would lift (2026-10-12 review, T-41).
        _ = await machine.UnsuppressAsync(CaptureStateReason.NoIndicator, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Running, paused or suppressed: a session under way, in which only the pill counts.</summary>
    private static bool UnderWay(SessionState state) =>
        state is SessionState.Recording or SessionState.Paused or SessionState.Suppressed;

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
