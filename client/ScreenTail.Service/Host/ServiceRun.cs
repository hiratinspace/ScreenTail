namespace ScreenTail.Service.Host;

/// <summary>
/// Runs the capture service's host until it stops, and says when it has (ST-112).
///
/// A stop can be asked before the host has finished starting: Windows' close arrives through the session-end
/// window, which exists before the host runs. The host then refuses to start by throwing
/// <see cref="OperationCanceledException"/>, which out of top-level code was a non-zero exit and a crash
/// report for what was an ordinary stop. A stop that was asked for ends the run with 0; any other
/// cancellation is still a failure.
/// </summary>
internal static class ServiceRun
{
    /// <param name="host">Disposed when this returns, so the store is closed by then.</param>
    /// <param name="stopAsked">Whether somebody asked the host to stop (the application lifetime's stopping token).</param>
    /// <param name="stopped">Completed when the run has ended, whatever the outcome, for the session-end window's wait.</param>
    public static async Task<int> RunAsync(IHost host, Func<bool> stopAsked, TaskCompletionSource stopped)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(stopAsked);
        ArgumentNullException.ThrowIfNull(stopped);
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stopAsked())
        {
            return 0;
        }
        finally
        {
            _ = stopped.TrySetResult();
        }
    }
}
