namespace ScreenTail.Core.Speech;

/// <summary>
/// How long the service's stop waits for the speech model to let go, and no longer (ST-112).
///
/// Windows ends a program five seconds after WM_ENDSESSION, and the service promises its store is closed
/// in four (<c>CloseRequests.DefaultBudget</c>). whisper.cpp takes seconds for a segment, and Whisper.net
/// will not free a model while a native call is still running on it, so a sign-out while the technician
/// was talking spent the whole budget waiting for a transcription nobody would read (laptop runs
/// 42031407710 and 42051212812, 2026-11-05). The owner's decision: the stop does not wait. What is still
/// running is left to finish on its own and free itself, or to go with the process.
/// </summary>
public static class StopBound
{
    /// <summary>The wait at stop. Long enough for a model that answers its abort, short against the budget.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Waits for <paramref name="work"/> for at most <paramref name="limit"/>.
    /// </summary>
    /// <returns>
    /// True when it finished inside the limit (and any fault it ended in is thrown). False when it was
    /// still running: it carries on, and a fault it ends in later is observed rather than left unobserved.
    /// </returns>
    public static async Task<bool> WithinAsync(Task work, TimeSpan limit, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work.WaitAsync(limit, time ?? TimeProvider.System).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }
    }
}
