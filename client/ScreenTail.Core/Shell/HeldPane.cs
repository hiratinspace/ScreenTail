namespace ScreenTail.Core.Shell;

/// <summary>
/// The Review pane the shell is holding, and every pane it has let go of until each has finished going
/// (T-169).
///
/// A Review pane's DisposeAsync is what writes the last of what was typed without waiting for the
/// autosave's pause, and commits a delete still inside its undo window and waits for its answer. Nothing
/// called it: a pane replaced by another session's, a pane whose session went away, and the pane on
/// screen when the window closed were all dropped, and with them the last edit and the pending delete.
///
/// A pane is let go of when it stops being the one held, never while it is: the shell shows the same
/// pane again on the way back from History, edits and all (T-159). What the content area holds that is
/// not a pane, a sentence, has nothing to let go of. Used on the shell's UI thread only.
/// </summary>
public sealed class HeldPane
{
    private readonly List<Task> _going = [];

    /// <summary>The pane being held, or null.</summary>
    public object? Current { get; private set; }

    /// <summary>Holds this pane, or nothing, and lets go of the one held before when it is another.</summary>
    public void Hold(object? pane)
    {
        if (ReferenceEquals(pane, Current))
        {
            return;
        }

        var previous = Current;
        Current = pane;
        LetGo(previous);
    }

    /// <summary>
    /// Lets go of a pane that is not being held: the one just replaced, or one that arrived for a load
    /// that was superseded or outlived the window. The pane being held is left alone.
    /// </summary>
    public void LetGo(object? pane)
    {
        if (pane is not IAsyncDisposable going || ReferenceEquals(pane, Current))
        {
            return;
        }

        _ = _going.RemoveAll(task => task.IsCompleted);
        _going.Add(GoAsync(going));
    }

    /// <summary>
    /// Lets go of the held pane as well, and finishes once every pane let go of has finished: the last
    /// write sent and the delete's answer read, or given up on by the pane's own bound.
    /// </summary>
    public Task ReleaseAsync()
    {
        Hold(null);
        return Task.WhenAll([.. _going]);
    }

    private static async Task GoAsync(IAsyncDisposable pane)
    {
        try
        {
            await pane.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A pane failing on its way out must not stop the others, or a close or a quit.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing is left on screen to tell. The note's own save reports a write it could not make
            // while the pane is up, and the service keeps what it was last sent.
        }
    }
}
