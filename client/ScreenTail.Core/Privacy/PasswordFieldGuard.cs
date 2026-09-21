using ScreenTail.Core.Audit;
using ScreenTail.Core.Sessions;
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

    /// <summary>True while this guard is holding capture suppressed.</summary>
    public bool Holding { get; private set; }

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
        var focused = Read();
        if (focused == FocusedField.Unknown)
        {
            Unknowns++;
            return Holding;
        }

        // Re-derived from the session rather than trusted, because Holding is only this guard's belief.
        // Focus a password field, pause and resume: the session is Recording again over the same field,
        // and a guard that believed it was already holding did nothing until focus moved. The same goes
        // for a new session begun over a field that still has focus, and for the other guard lifting a
        // suppression the two of them share. SensitiveContextGuard learned this first.
        if (Holding && machine.State is not SessionState.Suppressed)
        {
            Holding = false;
        }

        var wanted = focused == FocusedField.Password;
        if (wanted == Holding)
        {
            return Holding;
        }

        if (wanted)
        {
            // Suppress fails when the session is paused, stopped or idle, and correcting that is not this
            // guard's business. But then it holds nothing and must say so, or the next tick would "resume"
            // a session it never suppressed.
            Holding = await machine.SuppressAsync(CaptureStateReason.PasswordField, ct).ConfigureAwait(false);
            if (Holding)
            {
                Holds++;
            }
        }
        else
        {
            // Whether or not this moves the session — the technician may have paused meanwhile, which
            // leaves Unsuppress a no-op — the hold is over. Capture resumes with the next click, because
            // everything the hooks saw during the hold was dropped as it was drained.
            _ = await machine.UnsuppressAsync(CaptureStateReason.PasswordField, ct).ConfigureAwait(false);
            Holding = false;
        }

        return Holding;
    }

    /// <summary>How many passes ended in an exception. A rising count means the guard is struggling, not gone.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Told when a pass fails, so the host can log it. The type only, never the message (INV-10).</summary>
    public event Action<Exception>? Failed;

    public Task RunAsync(CancellationToken ct) => ResilientLoop.RunAsync(
        next: async token =>
        {
            _ = await _focusMoved.WaitAsync(_options.PollEvery, token).ConfigureAwait(false);
            return true;
        },
        step: TickAsync,
        onFailure: failure =>
        {
            _ = Interlocked.Increment(ref _failures);
            Failed?.Invoke(failure);
        },
        ct);

    public void Dispose() => _focusMoved.Dispose();

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
