namespace ScreenTail.Core.Net;

/// <summary>
/// The start-up order of the device's token, the tenant's policy and the outbox (2026-10-12 review, S-31).
///
/// The policy fetch and the first token refresh used to start side by side. The fetch usually went first,
/// found no token, and waited ten minutes, while the outbox drained every 30 seconds under the policy
/// cached on disk, a file the user can write. Now the fetch waits for a token, and the outbox waits for
/// the fetch. Both signals are set once and never reset.
///
/// In Core, so every platform's suite runs it: nothing here needs Windows.
/// </summary>
public static class StartupOrder
{
    /// <summary>
    /// The device's renewal loop, on DeviceSession's own schedule, that also says when there is first a
    /// token to send.
    /// </summary>
    /// <param name="failed">Told of a renewal that threw, so the host can log its type. Never its message (INV-10).</param>
    public static async Task RenewDeviceAsync(DeviceSession device, TaskCompletionSource tokenReady, TimeProvider time, CancellationToken ct, Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(tokenReady);
        while (!ct.IsCancellationRequested)
        {
            bool renewed;
            try
            {
                renewed = await device.RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed renewal is not the end of renewing.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // A credential save that throws fails the call after the new access token is kept, by
                // design. Ending the loop here meant no token was ever announced, so the policy was never
                // fetched and the outbox never sent again until a restart. Retried on the failure
                // schedule, and the token in hand is still announced below.
                failed?.Invoke(ex);
                renewed = false;
            }

            // Renewed, or a token the device already had (an activation, or the environment's fallback).
            if (device.CurrentToken is not null)
            {
                _ = tokenReady.TrySetResult();
            }

            try
            {
                await Task.Delay(renewed ? DeviceSession.RefreshEvery : DeviceSession.RetryAfterFailure, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The tenant policy loop, on PolicySync's own schedule, started once there is a token and saying
    /// when the first fetch landed. The outbox waits for that.
    /// </summary>
    /// <param name="failed">Told of a fetch that threw, so the host can log its type. Never its message (INV-10).</param>
    public static async Task SyncPolicyAsync(PolicySync policy, Task tokenReady, TaskCompletionSource fetched, TimeProvider time, CancellationToken ct, Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(tokenReady);
        ArgumentNullException.ThrowIfNull(fetched);
        try
        {
            await tokenReady.WaitAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                bool ok;
                try
                {
                    ok = await policy.FetchAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // One failed fetch is not the end of fetching.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    // Usually an apply that threw. PolicySync leaves the old policy in force then, so the
                    // next fetch applies this one again; until one lands nothing counts as fetched and the
                    // outbox keeps waiting (INV-8, INV-11). Ending the loop here wedged the outbox for the
                    // life of the service.
                    failed?.Invoke(ex);
                    ok = false;
                }

                if (ok)
                {
                    // Changed has been raised and applied by now, so whatever waits on this sees the
                    // tenant's policy in force rather than the cached copy.
                    _ = fetched.TrySetResult();
                }

                await Task.Delay(ok ? PolicySync.Every : PolicySync.RetryAfterFailure, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
