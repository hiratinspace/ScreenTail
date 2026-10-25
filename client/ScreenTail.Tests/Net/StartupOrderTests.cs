using System.Net;
using System.Net.Http.Json;
using System.Text;
using ScreenTail.Core.Net;

namespace ScreenTail.Tests.Net;

/// <summary>
/// The start-up order of the device's token, the tenant's policy and the outbox (2026-10-12 review, S-31).
///
/// The policy fetch and the first token refresh used to start side by side. The fetch usually went first,
/// found no token, and waited ten minutes, while the outbox drained every 30 seconds under the policy
/// cached on disk, a file the user can write. Now the fetch waits for a token, and the outbox waits for
/// the fetch. No network: a handler answers in memory. Nothing here waits on the clock: each step is
/// awaited on the signal it produces, and "the loop is waiting for its next turn" is the moment it asks
/// the clock for a timer.
///
/// Moved from the Windows suite: nothing here needs Windows, and every platform's suite now runs it.
/// </summary>
public sealed class StartupOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThePolicyIsFetchedOnceThereIsATokenAndOnlyThenSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PolicyBackend();
        using var http = new HttpClient(backend) { BaseAddress = new Uri("https://api.screentail.example/") };
        string? token = null;
        var policy = new PolicySync(http, () => token, new NoCache());
        var tokenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var syncing = StartupOrder.SyncPolicyAsync(policy, tokenReady.Task, fetched, TimeProvider.System, stopping.Token);

        // No token yet: nothing has been asked, and nothing says the policy is in force.
        Assert.Equal(0, backend.Asked);
        Assert.False(fetched.Task.IsCompleted);

        token = "an-access-token";
        tokenReady.SetResult();
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(1, backend.Asked);
        Assert.Equal("Bearer an-access-token", backend.Authorization);
        Assert.Equal("v9", policy.Current.Version);
        Assert.True(policy.Current.LocalOnlyLocked);

        await stopping.CancelAsync();
        await syncing.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task TheRenewalLoopSaysWhenThereIsATokenToSend()
    {
        // A device with no refresh token of its own, run on the environment's fallback token (the M1
        // runbook's way): the refresh has nothing to buy, and there is still a token to fetch with.
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient(new PolicyBackend()) { BaseAddress = new Uri("https://api.screentail.example/") };
        var device = new DeviceSession(http, new DeviceSessionTests.InMemoryCredentials(), () => "fallback-token", TimeProvider.System);
        var tokenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var renewing = StartupOrder.RenewDeviceAsync(device, tokenReady, TimeProvider.System, stopping.Token);
        await tokenReady.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        await stopping.CancelAsync();
        await renewing.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task WithNoTokenThePolicyIsNeverAskedForAndTheLoopStillStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new PolicyBackend();
        using var http = new HttpClient(backend) { BaseAddress = new Uri("https://api.screentail.example/") };
        var policy = new PolicySync(http, () => null, new NoCache());
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var syncing = StartupOrder.SyncPolicyAsync(policy, new TaskCompletionSource().Task, fetched, TimeProvider.System, stopping.Token);
        await stopping.CancelAsync();
        await syncing.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, backend.Asked);
        Assert.False(fetched.Task.IsCompleted);
    }

    [Fact]
    public async Task ARenewalWhoseSaveThrowsStillSaysThereIsATokenAndKeepsRenewing()
    {
        // DeviceSession keeps the new access token and then saves the credential; a save that throws
        // (antivirus holding the file, the ordinary case) fails the call by design. The loop used to end
        // there, before it said there was a token, so the policy was never fetched and the outbox, which
        // waits for that fetch, never sent anything again for the life of the service.
        var ct = TestContext.Current.CancellationToken;
        var backend = new PolicyBackend();
        using var http = new HttpClient(backend) { BaseAddress = new Uri("https://api.screentail.example/") };
        var device = new DeviceSession(http, new SaveThrows(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0)), time: TimeProvider.System);
        var tokenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new TimerSignal();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var renewing = StartupOrder.RenewDeviceAsync(device, tokenReady, time, stopping.Token);

        Assert.Same(tokenReady.Task, await Task.WhenAny(tokenReady.Task, renewing).WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Same(time.Waiting, await Task.WhenAny(time.Waiting, renewing).WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.False(renewing.IsCompleted);

        await stopping.CancelAsync();
        await renewing.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task AFetchThatThrowsNeitherEndsThePolicyLoopNorCountsAsFetched()
    {
        // Applying a new policy runs the host's handlers. One that throws used to end the loop for the
        // life of the service: no hourly policy, and an outbox that never drains. It must not end the
        // loop, and it must not count as fetched either, because the policy was not applied (INV-11).
        var ct = TestContext.Current.CancellationToken;
        var backend = new PolicyBackend();
        using var http = new HttpClient(backend) { BaseAddress = new Uri("https://api.screentail.example/") };
        var policy = new PolicySync(http, () => "an-access-token", new NoCache());
        policy.Changed += _ => throw new InvalidOperationException("an apply that failed");
        var tokenReady = new TaskCompletionSource();
        tokenReady.SetResult();
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new TimerSignal();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var syncing = StartupOrder.SyncPolicyAsync(policy, tokenReady.Task, fetched, time, stopping.Token);

        Assert.Same(time.Waiting, await Task.WhenAny(time.Waiting, syncing).WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Equal(1, backend.Asked);
        Assert.Equal(PolicySync.RetryAfterFailure, time.FirstDue);
        Assert.False(fetched.Task.IsCompleted);
        Assert.False(syncing.IsCompleted);

        await stopping.CancelAsync();
        await syncing.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task APolicyWhoseApplyThrowsIsNotInForceAndIsAppliedAgainNextTime()
    {
        // Fail closed. The policy is in force only once applied: a fetch whose apply threw leaves the
        // policy as it was, so the next fetch of the same answer raises Changed again rather than
        // finding nothing new and reporting a policy that was never applied as fetched.
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient(new PolicyBackend()) { BaseAddress = new Uri("https://api.screentail.example/") };
        var policy = new PolicySync(http, () => "an-access-token", new NoCache());
        var applies = 0;
        policy.Changed += _ =>
        {
            if (++applies == 1)
            {
                throw new InvalidOperationException("an apply that failed");
            }
        };

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => policy.FetchAsync(ct));
        Assert.Equal(TenantPolicy.Default.Version, policy.Current.Version);

        Assert.True(await policy.FetchAsync(ct));
        Assert.Equal(2, applies);
        Assert.Equal("v9", policy.Current.Version);
    }

    private sealed class PolicyBackend : HttpMessageHandler
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/v1/devices/token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "access-2", expiresAt = DateTimeOffset.UtcNow.AddHours(2) }),
                });
            }

            if (request.RequestUri?.AbsolutePath != "/v1/policy")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            _ = Interlocked.Increment(ref _asked);
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"version":"v9","retentionDays":7,"localOnly":true,"localOnlyLocked":true,"captureAllWindows":false}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class NoCache : IPolicyCache
    {
        public TenantPolicy? Load() => null;

        public void Save(TenantPolicy policy)
        {
        }
    }

    /// <summary>A credential store whose every save is refused, as one held open by antivirus is.</summary>
    private sealed class SaveThrows(DeviceCredential credential) : IDeviceCredentials
    {
        public DeviceCredential? Load() => credential;

        public void Save(DeviceCredential credential) => throw new IOException("held open");

        public void Clear()
        {
        }
    }

    /// <summary>
    /// The real clock, which also says when a loop first asks it for a timer: that is the loop waiting
    /// for its next turn, alive, rather than ended.
    /// </summary>
    private sealed class TimerSignal : TimeProvider
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Waiting => _waiting.Task;

        public TimeSpan? FirstDue { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!_waiting.Task.IsCompleted)
            {
                FirstDue = dueTime;
                _ = _waiting.TrySetResult();
            }

            return System.CreateTimer(callback, state, dueTime, period);
        }
    }
}
