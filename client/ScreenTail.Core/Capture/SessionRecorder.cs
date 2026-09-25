using ScreenTail.Core.Detection;
using ScreenTail.Core.Input;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Capture;

/// <summary>
/// Turns captured input into timeline events and screenshots, under the capture rules (ST-025, ST-048).
///
/// This is where the rules meet: the state machine decides whether anything may be recorded at all
/// (INV-6), the scope decision decides whether this particular window may be photographed (INV-5), and
/// the debouncer decides whether a click is a new action or part of one already captured.
///
/// A click that fails the scope test still becomes an event, with no frame: ST-023 asks for exactly
/// that, so Review can show "clicks logged, no frames" rather than a silent gap. Anything
/// keyboard-derived is dropped instead — INV-6 says out-of-scope means "no typing events written", and a
/// keystroke count taken in a customer's password manager is not made harmless by having no picture
/// beside it.
///
/// It lives in Core, away from the Windows hooks that feed it, because that is what it took to make the
/// rule testable. While the same code sat inside the Windows drain loop, no test project referenced it:
/// the out-of-scope drop could be deleted and the whole suite stayed green (weaknesses P0-4). The hooks
/// and the screen grab are still Windows-only and still behind interfaces; the decisions are here.
/// </summary>
public sealed class SessionRecorder(
    SessionMachine machine,
    IScreenshotCapturer capturer,
    Func<ScopeDecision?> currentScope,
    ClickDebouncer? debouncer = null)
{
    private readonly ClickDebouncer _debouncer = debouncer ?? new ClickDebouncer();
    private readonly InputSignalReader _reader = new();
    private long _dropped;
    private long _beforeSession;

    /// <summary>Keyboard signals and events dropped because the window was out of scope (INV-6). Counts only.</summary>
    public long DroppedOutOfScope => Interlocked.Read(ref _dropped);

    /// <summary>Signals stamped before the session began, and so not part of it. Counts only.</summary>
    public long DroppedBeforeSession => Interlocked.Read(ref _beforeSession);

    /// <summary>Clicks that shared a frame with an earlier one (ST-025).</summary>
    public long ClicksSharingAFrame => _debouncer.Suppressed;

    /// <summary>Raised after a frame is staged, so the host can log what it cost. Never carries pixels.</summary>
    public event Action<CapturedFrame>? FrameStaged;

    /// <summary>Raised when the window was gone by the time the picture was taken. A missing frame, never a blank one.</summary>
    public event Action<long>? FrameMissed;

    /// <summary>A new session starts with no history, so its first click is captured (ST-029).</summary>
    public void Reset()
    {
        _debouncer.Reset();

        // Whatever was being typed belonged to no session, or to the last one.
        _reader.Discard();
    }

    /// <summary>
    /// Records what the input hooks saw since the last call, under the rules in force <i>now</i>.
    ///
    /// Signals rather than finished events, because the rules have to be applied before keys are added
    /// up. A typing burst is a count kept across many calls, and an event that says "fourteen characters"
    /// cannot say that they were typed while capture was suppressed for a password field. Until
    /// 2026-09-19 that is what happened: the count ran through pauses, suppression and out-of-scope
    /// windows, and the first click after capture came back wrote it into the session — the length of a
    /// password and the moment it was typed, recorded by the mechanism built not to (INV-6).
    ///
    /// So, per call: if the session is not recording, everything is dropped and the open burst is
    /// forgotten. If the window in front may not have its typing recorded, keyboard signals are dropped
    /// and the open burst is forgotten; clicks still pass, as ST-023 asks. A burst that was legitimately
    /// under way when a boundary arrived is lost with the rest. That costs a note one "typed 9
    /// characters", and the alternative is deciding after the fact which keys were which.
    ///
    /// Anything stamped before the session began is dropped too. The click that brings a remote tool to
    /// the front is what starts the session, so it arrives here with a negative session time; the store
    /// refuses one, and that exception used to end click capture for the life of the service.
    /// </summary>
    public async Task RecordSignalsAsync(
        ReadOnlyMemory<InputSignal> signals,
        Func<long, long> toSessionMs,
        Func<long, long, TimeSpan> elapsed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toSessionMs);
        ArgumentNullException.ThrowIfNull(elapsed);

        if (machine.State != SessionState.Recording)
        {
            _reader.Discard();
            return;
        }

        // The same question RecordAsync asks of a finished event, asked of the keys before they are
        // counted. Nothing known about the window in front is not a reason to count keystrokes.
        var typingAllowed = currentScope()?.MayCaptureFrames == true;
        if (!typingAllowed)
        {
            _reader.Discard();
        }

        var kept = new List<InputSignal>(signals.Length);
        foreach (var signal in signals.Span)
        {
            if (toSessionMs(signal.Timestamp) < 0)
            {
                _ = Interlocked.Increment(ref _beforeSession);
                continue;
            }

            if (!typingAllowed && signal.Kind != InputKind.Click)
            {
                _ = Interlocked.Increment(ref _dropped);
                continue;
            }

            kept.Add(signal);
        }

        if (kept.Count == 0)
        {
            return;
        }

        var events = _reader.Read(kept.ToArray(), toSessionMs, elapsed);
        await RecordAsync([.. events], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a batch of events. Each is checked against the scope decision in force, and a click that
    /// survives may also produce a frame.
    /// </summary>
    public async Task RecordAsync(IReadOnlyList<SessionEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (machine.State != SessionState.Recording)
        {
            // Paused, suppressed or idle: the signals are dropped rather than held, so nothing observed
            // while capture was off can arrive late and land in the session (INV-6).
            return;
        }

        var scope = currentScope();
        foreach (var sessionEvent in events)
        {
            // INV-6, decided by the scope decision itself so the rule is one thing in one place. No scope
            // decision yet means nothing is known about the window in front, which is not a reason to
            // record a keystroke count.
            if (scope is null ? IsKeyboard(sessionEvent) : !scope.MayRecord(sessionEvent))
            {
                _ = Interlocked.Increment(ref _dropped);
                continue;
            }

            _ = await machine.TryRecordEventAsync(sessionEvent, ct).ConfigureAwait(false);
            if (sessionEvent is ClickEvent click)
            {
                await CaptureForAsync(click, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Keyboard-derived events, which INV-6 calls "typing events". A shortcut and an Enter say as much
    /// about what someone typed as a burst count does — Ctrl+C in a password manager is the case — so all
    /// three are treated alike.
    /// </summary>
    private static bool IsKeyboard(SessionEvent sessionEvent) =>
        sessionEvent is TypingBurstEvent or ShortcutEvent or EnterEvent;

    private async Task CaptureForAsync(ClickEvent click, CancellationToken ct)
    {
        // Re-read rather than reuse the decision from the top of the batch: the technician may have moved
        // to another window between two events, and a picture is taken against the window scope is about.
        var scope = currentScope();
        if (scope is null || !scope.MayCaptureFrames)
        {
            return;
        }

        if (!_debouncer.ShouldCapture())
        {
            return;
        }

        var frame = capturer.CaptureForegroundWindow(scope.Window);
        if (frame is null)
        {
            FrameMissed?.Invoke(click.TsMs);
            return;
        }

        // Staged with redaction_pending, the only state a frame can be born in: nothing outside the
        // redaction worker can read one until it has been through ST-041 (INV-1).
        _ = await machine.TryStageFrameAsync(
            new StagedFrame(
                Guid.NewGuid().ToString("N")[..12],
                click.TsMs,
                FrameTrigger.Click,
                frame.Width,
                frame.Height,
                new Point { X = click.X, Y = click.Y },
                frame.Image),
            ct).ConfigureAwait(false);

        FrameStaged?.Invoke(frame);
    }
}
