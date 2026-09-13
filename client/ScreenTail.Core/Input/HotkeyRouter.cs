using ScreenTail.Core.Sessions;

namespace ScreenTail.Core.Input;

/// <summary>
/// Turns a chord into the thing it does (ST-029).
///
/// Pause is the one that has to be quick: Spec §5 S1 puts the chord on the HUD's own pill ("Paused — press
/// Ctrl+Alt+P to resume"), so the technician is looking straight at the thing that has to change. The state
/// machine broadcasts on transition, so the work here is to stay out of the way — no waiting on a capture
/// source, no store round-trip beyond the transition itself.
///
/// <b>Pause and resume share a chord</b> because a technician pausing to type a password is going to press
/// the same keys again to carry on, and a separate resume chord is one more thing to remember at exactly
/// the wrong moment. Suppressed is deliberately not resumable this way: that state belongs to the password
/// guard and the login heuristic, and a chord that overrode them would be a way to turn a privacy control
/// off by accident.
/// </summary>
public sealed class HotkeyRouter(SessionMachine machine, Func<CancellationToken, Task<bool>>? startCapture = null)
{
    public async Task<bool> InvokeAsync(HotkeyAction action, CancellationToken ct = default) => action switch
    {
        HotkeyAction.StartCapture => startCapture is null
            ? false
            : await startCapture(ct).ConfigureAwait(false),
        HotkeyAction.PauseOrResume => await PauseOrResumeAsync(ct).ConfigureAwait(false),
        HotkeyAction.StopAndDraft => await machine.StopAsync(ct).ConfigureAwait(false),

        // Bypasses nothing and needs to bypass nothing: the marker path never went through the click
        // debouncer, so "regardless of debounce" is a property of the design rather than a special case.
        HotkeyAction.MarkMoment => await machine.MarkMomentAsync(ct).ConfigureAwait(false),
        HotkeyAction.DiscardSession => await machine.DiscardAsync(ct).ConfigureAwait(false),
        _ => false,
    };

    private Task<bool> PauseOrResumeAsync(CancellationToken ct) => machine.State switch
    {
        SessionState.Recording => machine.PauseAsync(ct),
        SessionState.Paused => machine.ResumeAsync(ct),
        _ => Task.FromResult(false),
    };
}
