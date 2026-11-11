namespace ScreenTail.Core.Shell;

/// <summary>
/// Lets a screen's action run once at a time, for a command that asks a question, opens a dialog or
/// sends something that cannot be taken back.
///
/// The toolkit's async command reports that it cannot run while it is running, which is what turns the
/// button off, but its Execute does not look. A second invoke that does not go through the button (an
/// access key, UI Automation's Invoke, a queued click, a shortcut) ran the action again under the first,
/// and asked twice. The second run also replaced the task the command watches, so the button came back
/// on while the first question was still up (hosted run on #274). So the action holds its own flag, and
/// the command's CanExecute reads <see cref="Busy"/> too.
///
/// <see cref="Busy"/> is set before the action starts, so a dialog the action shows before its first
/// await (a nested message loop, where a queued invoke can arrive) is covered as well.
///
/// Read and written on the UI thread only: the continuation stays on the caller's context, so
/// <c>changed</c> runs where the command's CanExecuteChanged may be raised.
/// </summary>
public sealed class OneAtATime
{
    /// <summary>The action is running: its question is up or its answer is on its way.</summary>
    public bool Busy { get; private set; }

    /// <summary>
    /// Runs <paramref name="action"/> unless it is already running, in which case nothing happens.
    /// <paramref name="changed"/> is called when <see cref="Busy"/> turns on and again when it turns off,
    /// for the command to say its CanExecute moved.
    /// </summary>
    public async Task RunAsync(Func<Task> action, Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Busy)
        {
            return;
        }

        Busy = true;
        changed?.Invoke();
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            Busy = false;
            changed?.Invoke();
        }
    }
}
