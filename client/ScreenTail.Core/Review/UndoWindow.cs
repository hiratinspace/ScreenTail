namespace ScreenTail.Core.Review;

/// <summary>
/// An irreversible act, held open long enough to take back (Spec §4: undo over confirm).
///
/// Spec §3 reserves the typed confirmation for discarding a whole session. Everything smaller — deleting
/// a screenshot, blurring a region — happens at once and offers a few seconds to undo, because a dialog
/// in front of every small destructive act trains people to dismiss dialogs, including the one that
/// mattered.
///
/// The ordering is the part worth arguing about, and it is the same for both users of this class: the
/// safe outcome happens <em>immediately</em>, and what is held for the undo window is only the means to
/// put it back. A technician who blurs a password needs those pixels gone now, not in five seconds and
/// not "unless the process died" — so the blurred image is written straight away and the original is kept
/// in memory alone, where a crash loses it, which is the direction a crash should fail in.
///
/// Nothing here runs a timer: <see cref="Tick"/> is called by whatever already ticks, so the whole rule is
/// testable on a clock the test moves by hand (ADR-0002).
/// </summary>
/// <typeparam name="T">What has to be kept in order to undo — the original bytes, the deleted frame.</typeparam>
public sealed class UndoWindow<T>
{
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Action<T> _commit;
    private readonly Action<T> _undo;
    private T? _held;
    private long _stagedAt;

    /// <param name="commit">Called once the window closes. Whatever is held is now unrecoverable.</param>
    /// <param name="undo">Called when the technician takes it back, with the thing that was held.</param>
    /// <param name="window">Spec §5 S3 says 5 s for a deleted screenshot.</param>
    public UndoWindow(
        Action<T> commit,
        Action<T> undo,
        TimeSpan? window = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(undo);
        _commit = commit;
        _undo = undo;
        _window = window ?? TimeSpan.FromSeconds(5);
        _time = time ?? TimeProvider.System;
    }

    public bool IsOpen => _held is not null;

    /// <summary>What the toast says it will undo, or null when there is nothing to take back.</summary>
    public T? Pending => _held;

    /// <summary>
    /// The act has already happened; this holds what would put it back.
    ///
    /// A second act while one is pending closes the first for good rather than replacing it. Two overlapping
    /// undos need two toasts and an order, and a technician deleting four frames in a row would otherwise
    /// find that only the last one was ever undoable — with the other three silently unrecoverable and no
    /// toast to say so.
    /// </summary>
    public void Stage(T undoable)
    {
        ArgumentNullException.ThrowIfNull(undoable);
        if (_held is { } outstanding)
        {
            _commit(outstanding);
        }

        _held = undoable;
        _stagedAt = _time.GetTimestamp();
    }

    /// <summary>The toast's Undo. Returns whether there was still anything to take back.</summary>
    public bool Undo()
    {
        if (_held is not { } held)
        {
            return false;
        }

        _held = default;
        _undo(held);
        return true;
    }

    /// <summary>Call on the same timer as everything else. Closes the window once it has run out.</summary>
    public void Tick()
    {
        if (_held is { } held && _time.GetElapsedTime(_stagedAt) >= _window)
        {
            _held = default;
            _commit(held);
        }
    }

    /// <summary>
    /// Closes the window now — the pane lost focus, or the window is closing. Spec §5 S3 ends the blur's
    /// undo when the pane loses focus, and a held original that outlives the screen it belongs to is a
    /// copy of something a technician asked to destroy.
    /// </summary>
    public void Close()
    {
        if (_held is { } held)
        {
            _held = default;
            _commit(held);
        }
    }
}
