namespace ScreenTrail.Core.Net;

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
    ///
    /// Until that first token, each turn signs in for the policy fetch that is waiting on it, which
    /// local-only lets out because the policy is how local-only is lifted; under local-only one that fails
    /// is tried again on the policy fetch's failure schedule (<see cref="PolicySync.RetryAfterFailure"/>),
    /// so it goes no more often than the fetch it is for. After it, a turn renews in the
    /// background only while <paramref name="inBackground"/> says it may, and under local-only it may
    /// not: there the token is renewed when a publish or the hourly policy fetch asks for one
    /// (<see cref="DeviceSession.EnsureTokenAsync"/>), never on the timer (INV-8, 2026-11-15 review).
    /// </summary>
    /// <param name="failed">Told of a renewal that threw, so the host can log its type. Never its message (INV-10).</param>
    /// <param name="inBackground">Whether a renewal nobody asked for may go now. Null means always, as before.</param>
    public static async Task RenewDeviceAsync(DeviceSession device, TaskCompletionSource tokenReady, TimeProvider time, CancellationToken ct, Action<Exception>? failed = null, Func<bool>? inBackground = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(tokenReady);
        while (!ct.IsCancellationRequested)
        {
            bool renewed;
            var forThePolicy = false;
            try
            {
                if (!tokenReady.Task.IsCompleted)
                {
                    // The sign-in the first policy fetch waits for.
                    forThePolicy = device.Activated;
                    renewed = await device.RefreshAsync(EgressPurpose.Policy, ct).ConfigureAwait(false);
                }
                else if (inBackground is null || inBackground())
                {
                    renewed = await device.RefreshAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    // Local-only: no request at all, rather than one the guard refuses and counts. The
                    // next turn looks again, so a lifted local-only renews within minutes.
                    renewed = false;
                }
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

            // Under local-only a failed sign-in for the policy is retried on the policy fetch's own failure
            // schedule, not the renewal's: it goes out only as often as the fetch it is for would (INV-8).
            // Before activation nothing was sent, so the shorter wait only looks again sooner.
            var wait = renewed
                ? DeviceSession.RefreshEvery
                : forThePolicy && inBackground is not null && !inBackground()
                    ? PolicySync.RetryAfterFailure
                    : DeviceSession.RetryAfterFailure;
            try
            {
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
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
    /// <param name="signIn">
    /// Asked before each fetch for a token that has not run out, renewing it on behalf of the policy fetch
    /// when it has. Under local-only the background renewal does not run, so this is how the hourly fetch
    /// still has a token to send. Null fetches with whatever token is in hand, as before.
    /// </param>
    public static async Task SyncPolicyAsync(PolicySync policy, Task tokenReady, TaskCompletionSource fetched, TimeProvider time, CancellationToken ct, Action<Exception>? failed = null, Func<CancellationToken, Task>? signIn = null)
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
                    if (signIn is not null)
                    {
                        await signIn(ct).ConfigureAwait(false);
                    }

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
                    // Usually an apply that threw. PolicySync remembers the policy as not yet applied, so
                    // the next fetch applies it again; until one lands nothing counts as fetched and the
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
