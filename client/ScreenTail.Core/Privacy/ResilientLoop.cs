namespace ScreenTail.Core.Privacy;

/// <summary>
/// A loop that outlives its own bad moments (INV-6; 2026-09-19 review).
///
/// The loops that keep capture honest — the password-field guard, the sensitive-context guard, the scope
/// coordinator, the scene sampler — each caught cancellation and nothing else. One exception of any
/// other kind, and a full disk under a state write is enough, ended the loop for the life of the
/// service. Nothing else noticed, and capture carried on without the thing that was supposed to stop it.
/// A dead password guard means password fields are recorded; a dead scope coordinator leaves the last
/// scope decision standing for every window that follows. They failed open, and silently.
///
/// One place for the rule, so that the next loop gets it by calling this rather than by remembering.
/// </summary>
public static class ResilientLoop
{
    /// <param name="next">Waits for the next turn. False ends the loop, as a finished timer does.</param>
    /// <param name="step">One turn. An exception costs this turn and no other.</param>
    /// <param name="onFailure">
    /// Told what went wrong, so it can be counted and logged. Log the type and never the message: a
    /// store error can quote what it was asked to write (INV-10). May itself throw without ending the loop.
    /// </param>
    public static async Task RunAsync(
        Func<CancellationToken, Task<bool>> next,
        Func<CancellationToken, Task> step,
        Action<Exception>? onFailure,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(step);

        try
        {
            while (await next(ct).ConfigureAwait(false))
            {
                try
                {
                    await step(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The whole point: no exception from a step may end the loop.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // Including an OperationCanceledException nobody asked for, which is what a call that
                    // timed out throws. Reading every one of those as "stop" is how a loop exits on a slow
                    // disk.
                    Report(onFailure, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static void Report(Action<Exception>? onFailure, Exception failure)
    {
        try
        {
            onFailure?.Invoke(failure);
        }
#pragma warning disable CA1031 // A broken logger is not a reason to stop guarding password fields.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
