using System.Net;
using System.Net.Http;
using System.Text;
using ScreenTail.Core.Net;
using ScreenTail.Service.Host;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// The start-up order of the device's token, the tenant's policy and the outbox (2026-10-12 review, S-31).
///
/// The policy fetch and the first token refresh used to start side by side. The fetch usually went first,
/// found no token, and waited ten minutes, while the outbox drained every 30 seconds under the policy
/// cached on disk, a file the user can write. Now the fetch waits for a token, and the outbox waits for
/// the fetch. No desktop and no network: a handler answers in memory, so this runs on the hosted runner.
/// Nothing here waits on the clock: each step is awaited on the signal it produces.
/// </summary>
public sealed class PolicyStartupTests
{
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

        var syncing = CaptureHost.SyncPolicyAsync(policy, tokenReady.Task, fetched, TimeProvider.System, stopping.Token);

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
        var device = new DeviceSession(http, new NoCredentials(), () => "fallback-token", TimeProvider.System);
        var tokenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var renewing = CaptureHost.RenewDeviceAsync(device, tokenReady, TimeProvider.System, stopping.Token);
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

        var syncing = CaptureHost.SyncPolicyAsync(policy, new TaskCompletionSource().Task, fetched, TimeProvider.System, stopping.Token);
        await stopping.CancelAsync();
        await syncing.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(0, backend.Asked);
        Assert.False(fetched.Task.IsCompleted);
    }

    private sealed class PolicyBackend : HttpMessageHandler
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

    private sealed class NoCredentials : IDeviceCredentials
    {
        public DeviceCredential? Load() => null;

        public void Save(DeviceCredential credential)
        {
        }

        public void Clear()
        {
        }
    }
}
