namespace ScreenTail.Core.Shell;

/// <summary>
/// Quit, after the shell's Review panes have written their last (T-169).
///
/// A Review pane let go of writes what was typed since the autosave's last pause and commits a delete
/// still inside its undo window, and that takes a round trip to the service. Quit therefore closes the
/// window still open and waits for every shell window closed so far to finish that, the open one and any
/// closed a moment before, for at most <see cref="DefaultBound"/>. Then it shuts down, whatever happened:
/// a pane that failed, a pane still waiting on a service that has gone, or a window that would not close.
///
/// Quit happens once. A second Quit while the first is waiting gets the same task, rather than shutting
/// down at once and cutting the wait short. Used on the shell's UI thread only, and the shutdown runs back
/// on it. In Core, so the Mac suite holds the sequence.
/// </summary>
/// <param name="time">The clock the bound runs on.</param>
/// <param name="bound">How long Quit waits; <see cref="DefaultBound"/> unless a test says otherwise.</param>
public sealed class ShellQuit(TimeProvider? time = null, TimeSpan? bound = null)
{
    /// <summary>
    /// Longer than the filmstrip's own bound on a delete's answer, and short enough that Quit is still Quit
    /// when the pipe has gone.
    /// </summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _bound = bound ?? DefaultBound;
    private readonly List<Task> _closing = [];
    private Task? _quitting;

    /// <summary>A shell window closed. Quit waits for what its Review pane still has to write.</summary>
    public void WindowClosed(Task released)
    {
        ArgumentNullException.ThrowIfNull(released);
        _ = _closing.RemoveAll(task => task.IsCompleted);
        _closing.Add(released);
    }

    /// <summary>
    /// Closes the window still open, whose close reports here through <see cref="WindowClosed"/>, waits
    /// for every closed window's pane within the bound, and shuts down. The same task for every call.
    /// </summary>
    public Task QuitAsync(Action closeOpenWindow, Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(closeOpenWindow);
        ArgumentNullException.ThrowIfNull(shutdown);
        return _quitting ??= RunAsync(closeOpenWindow, shutdown);
    }

    private async Task RunAsync(Action closeOpenWindow, Action shutdown)
    {
        try
        {
            closeOpenWindow();

            // Back on the UI thread afterwards, because that is where the application shuts down.
            await Task.WhenAll([.. _closing]).WaitAsync(_bound, _time).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Quit is still Quit: a pane that failed or a window that would not close does not stop it.
        catch (Exception)
#pragma warning restore CA1031
        {
            // A pane still writing at the bound goes on until the process ends; nothing is left to tell.
        }
        finally
        {
            shutdown();
        }
    }
}
