using ScreenTail.Core.Audit;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <summary>What has keyboard focus, as far as the platform will say.</summary>
public enum FocusedField
{
    /// <summary>The platform answered, and it is not somewhere a credential is being typed.</summary>
    NotPassword,

    /// <summary>The platform answered: this field masks what is typed into it.</summary>
    Password,

    /// <summary>
    /// The platform would not say. Automation is unavailable, the window belongs to a process we cannot
    /// inspect, or focus is somewhere with no automation peer at all. Not the same as "no".
    /// </summary>
    Unknown,
}

/// <summary>Asks the platform what has focus. Windows answers with UI Automation; the Mac build has no answer.</summary>
public interface IFocusedFieldProbe
{
    FocusedField Read();
}

public sealed record PasswordFieldOptions
{
    /// <summary>
    /// A backstop for focus changes that arrive as no event: a remote canvas redrawing, a control that
    /// never raises one, a hook that was removed. Without it, one missed "focus left" would leave capture
    /// suppressed for the rest of the session, and one missed "focus entered" would leave a password field
    /// capturable.
    /// </summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Stops capture while a password field has focus (ST-040, INV-6).
///
/// The suppression itself is already structural: <c>ClickCaptureLoop</c> drops every signal it drains
/// while the session is not recording, so nothing observed during a hold is written — not a frame, not a
/// typing burst. This class only has to decide when to hold.
///
/// <b>An unknown answer keeps the last decision rather than becoming a "no".</b> Automation fails
/// transiently and on windows we are not allowed to inspect, and suppressing on every failure would make
/// capture useless on a machine where automation is simply broken — silently, which is worse. What makes
/// that acceptable is that this is not the only thing looking: ST-041 reads the screen itself, so a
/// credential prompt whose field cannot be inspected still gets caught by the words around it. The two
/// mechanisms cover each other's blind spots, which is why neither has to be conservative to the point of
/// being unusable. <see cref="Unknowns"/> is counted so the diagnostics panel can say the probe is not
/// answering rather than leaving it to be discovered in a ticket.
/// </summary>
public sealed class PasswordFieldGuard(
    SessionMachine machine,
    IFocusedFieldProbe probe,
    TimeProvider? time = null,
    PasswordFieldOptions? options = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly PasswordFieldOptions _options = options ?? new PasswordFieldOptions();
    private readonly SemaphoreSlim _focusMoved = new(0);
    private long _failures;
    private long _passes;

    /// <summary>
    /// True while the session keeps this guard's hold, which a pause does not end. Asked of the session
    /// every time rather than remembered: a remembered hold went stale across a pause and across a
    /// restart, and was wrong in both directions (2026-10-12 review, T-41 and T-50).
    /// </summary>
    public bool Holding => machine.IsHeld(CaptureStateReason.PasswordField);

    /// <summary>How many separate times capture has been suppressed for a password field. Counts only (INV-10).</summary>
    public int Holds { get; private set; }

    /// <summary>How often the platform would not say what had focus. A rising count means the probe is broken.</summary>
    public long Unknowns { get; private set; }

    /// <summary>
    /// Keyboard focus moved. Called from the focus hook, so it does nothing but wake the loop: the probe
    /// is a cross-process call and has no business running on a message pump.
    /// </summary>
    public void FocusMoved()
    {
        if (_focusMoved.CurrentCount == 0)
        {
            _ = _focusMoved.Release();
        }
    }

    /// <summary>
    /// One pass: ask what has focus and bring the session into line with the answer. Separate from
    /// <see cref="RunAsync"/> so a test can step it without a clock or a hook.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        _ = Interlocked.Increment(ref _passes);

        // Nothing to hold, so nothing to ask about.
        //
        // Read() is a cross-process UI Automation call -- ADR-0001 measured p95 9.5-21 ms and a worst
        // case above 100 ms -- and this made it before looking at the session at all, once a second, all
        // day, on a machine that spends most of its day not recording. That is one to two per cent of a
        // core against ST-031's one per cent idle budget, spent deciding nothing (2026-09-20 review).
        //
        // Suppressed as well as Recording: a hold has to be able to end. And Paused, because a paused
        // session is one Resume away from recording. Focus leaving the field during a pause has to let go
        // of the hold, or Resume goes back to a suppression nothing will lift (T-41); focus arriving has
        // to take one, or Resume goes to Recording over the field until the next probe answers
        // (2026-10-12 review). The saving is the idle day, not the paused minutes. Holding covers the
        // moment a session is leaving, when a hold can still be in place.
        if (machine.State is not (SessionState.Recording or SessionState.Suppressed or SessionState.Paused) && !Holding)
        {
            return false;
        }

        var focused = Read();
        if (focused == FocusedField.Unknown)
        {
            Unknowns++;
            return Holding;
        }

        // Asked of the session, not remembered. A new session begun over a field that still has focus
        // has no hold yet, whatever this guard did in the last one; a paused session still has its hold.
        var held = Holding;
        var wanted = focused == FocusedField.Password;
        if (wanted == held)
        {
            return held;
        }

        if (wanted)
        {
            // Paused, the session records the hold and stays paused, so Resume goes straight to Suppressed.
            // Suppress fails when the session is stopped or idle, and correcting that is not this guard's
            // business. Then nothing is held, and the session says so.
            held = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct).ConfigureAwait(false);
            if (held)
            {
                Holds++;
            }

            return held;
        }

        // The hold is over, paused or not. Paused, this leaves the session paused and lets go of the
        // hold, so Resume does not go back to it. Otherwise capture resumes with the next click, because
        // everything the hooks saw during the hold was dropped as it was drained.
        _ = await machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>How many passes ended in an exception. A rising count means the guard is struggling, not gone.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>How many passes there have been, for any reason. Each is a wakeup (ST-031).</summary>
    internal long Passes => Interlocked.Read(ref _passes);

    /// <summary>Told when a pass fails, so the host can log it. The type only, never the message (INV-10).</summary>
    public event Action<Exception>? Failed;

    public Task RunAsync(CancellationToken ct)
    {
        // A session beginning is as good a reason to look as focus moving.
        //
        // TickAsync now does nothing unless a session is running, which would otherwise leave a session
        // that starts over an already-focused password field waiting up to a poll before anything
        // noticed. Waking on the transition closes that: the state change is the event, and the field
        // has not moved.
        machine.StateChanged += WakeOnStateChange;
        try
        {
            return ResilientLoop.RunAsync(
                next: async token =>
                {
                    // The poll is a backstop for a session (ST-031). With none, and no hold left to let
                    // go of, a pass decides nothing, and this woke once a second all day to decide it.
                    // A session starting releases the same signal a focus change does, so the wait
                    // ends the moment there is something to look at.
                    var backstop = SessionLoop.IsUnderWay(machine.State) || Holding ? _options.PollEvery : Timeout.InfiniteTimeSpan;
                    _ = await _focusMoved.WaitAsync(backstop, token).ConfigureAwait(false);
                    return true;
                },
                step: TickAsync,
                onFailure: failure =>
                {
                    _ = Interlocked.Increment(ref _failures);
                    Failed?.Invoke(failure);
                },
                ct);
        }
        finally
        {
            _stopWaking = () => machine.StateChanged -= WakeOnStateChange;
        }
    }

    private void WakeOnStateChange(CaptureStateSnapshot snapshot) => FocusMoved();

    private Action? _stopWaking;

    public void Dispose()
    {
        _stopWaking?.Invoke();
        _focusMoved.Dispose();
    }

    /// <summary>A probe that throws is a probe that did not answer, not a field that is safe to capture.</summary>
    private FocusedField Read()
    {
        try
        {
            return probe.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FocusedField.Unknown;
        }
    }
}
