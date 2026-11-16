using System.Net;
using System.Net.Http.Json;
using System.Text;
using ScreenTail.Core.Net;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Net;

/// <summary>
/// INV-8 for the device's own sign-in (2026-11-15 review).
///
/// Renewing the access token went out under the Publish purpose, which local-only lets through, so a
/// device under local-only still called the backend every fifty minutes on a timer. The sign-in has its
/// own purpose now and says what it is for: under local-only only a sign-in for a publish the technician
/// asked for, or for the policy fetch that can lift local-only, goes. These build the real session, the
/// real guard and the deployment's own egress shape, and count what reached the far side.
/// </summary>
public sealed class DeviceSignInEgressTests
{
    private static readonly Uri Backend = new("https://api.screentail.example/");
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, false)]
    [InlineData(EgressPurpose.Publish, true)]
    [InlineData(EgressPurpose.Policy, true)]
    [InlineData(EgressPurpose.Summarisation, false)]
    [InlineData(EgressPurpose.Backend, false)]
    [InlineData(EgressPurpose.ModelDownload, false)]
    [InlineData(EgressPurpose.UpdateCheck, false)]
    [InlineData(EgressPurpose.DeviceAuth, false)]
    public void UnderLocalOnlyASignInGoesOnlyForAPublishOrThePolicy(EgressPurpose? onBehalfOf, bool allowed)
    {
        var policy = Policy(localOnly: true);

        var decision = policy.Decide(EgressPurpose.DeviceAuth, new Uri(Backend, "v1/devices/token"), onBehalfOf);

        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(!allowed, decision.LocalOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EgressPurpose.Publish)]
    public void WithoutLocalOnlyASignInReachesTheBackendAndNowhereElse(EgressPurpose? onBehalfOf)
    {
        var policy = Policy(localOnly: false);

        Assert.True(policy.Decide(EgressPurpose.DeviceAuth, new Uri(Backend, "v1/devices/token"), onBehalfOf).Allowed);
        Assert.False(policy.Decide(EgressPurpose.DeviceAuth, new Uri("https://elsewhere.example/v1/devices/token"), onBehalfOf).Allowed);
        Assert.False(policy.Decide(EgressPurpose.DeviceAuth, new Uri("http://api.screentail.example/v1/devices/token"), onBehalfOf).Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnderLocalOnlyTheBackgroundRenewalSendsNothing(bool lockedByTheAdmin)
    {
        // The finding itself. Under the Publish purpose this reached the backend.
        var policy = Policy(localOnly: true, enforced: lockedByTheAdmin);
        var far = new TokenBackend();
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };
        var device = new DeviceSession(http, Activated(), time: new ManualTime(T0));

        Assert.False(await device.RefreshAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, far.TokenRequests);
        Assert.Equal(1, guard.Blocked);
        Assert.Null(device.CurrentToken);
        Assert.False(device.Revoked);
    }

    [Theory]
    [InlineData(EgressPurpose.Publish)]
    [InlineData(EgressPurpose.Policy)]
    public async Task UnderLocalOnlyASignInForAPublishOrThePolicyGoes(EgressPurpose onBehalfOf)
    {
        var policy = Policy(localOnly: true, enforced: true);
        var far = new TokenBackend();
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };
        var device = new DeviceSession(http, Activated(), time: new ManualTime(T0));

        Assert.True(await device.RefreshAsync(onBehalfOf, TestContext.Current.CancellationToken));

        Assert.Equal(1, far.TokenRequests);
        Assert.Equal("access-1", device.CurrentToken);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task UnderLocalOnlyActivatingWithAnInvitesCodeStillWorks()
    {
        // The technician typed the code and pressed Activate: a sign-in they asked for.
        var policy = Policy(localOnly: true, enforced: true);
        var far = new TokenBackend();
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };
        var device = new DeviceSession(http, new DeviceSessionTests.InMemoryCredentials(), time: new ManualTime(T0));

        var answer = await device.ActivateAsync("kx7pm-4r2wq", "TECH-LAPTOP", TestContext.Current.CancellationToken);

        Assert.True(answer.Ok, answer.Refusal);
        Assert.Equal(1, far.ActivateRequests);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task ATokenThatHasNotRunOutIsUsedAsItIsAndOneThatHasIsRenewedOnDemand()
    {
        var policy = Policy(localOnly: true, enforced: true);
        var far = new TokenBackend();
        using var http = new HttpClient(new EgressGuard(policy, far)) { BaseAddress = Backend };
        var time = new ManualTime(T0);
        var device = new DeviceSession(http, Activated(), time: time);

        Assert.Equal("access-1", await device.EnsureTokenAsync(EgressPurpose.Publish, TestContext.Current.CancellationToken));
        Assert.Equal("access-1", await device.EnsureTokenAsync(EgressPurpose.Publish, TestContext.Current.CancellationToken));
        Assert.Equal(1, far.TokenRequests);

        // Past the hour the token was good for: bought again when it is needed, not before.
        time.Advance(TimeSpan.FromHours(3));
        Assert.Equal("access-2", await device.EnsureTokenAsync(EgressPurpose.Publish, TestContext.Current.CancellationToken));
        Assert.Equal(2, far.TokenRequests);
    }

    [Fact]
    public async Task UnderLocalOnlyAPublishAfterALongIdleSignsInFirstAndPublishes()
    {
        // With no background renewal under local-only, the access token in hand has run out by the time
        // the technician publishes in the afternoon. The publish buys one first.
        var policy = Policy(localOnly: true, enforced: true);
        var far = new TokenBackend();
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };
        var time = new ManualTime(T0);
        var device = new DeviceSession(http, Activated(), time: time);
        var gateway = SigningInPsaGateway.For(new PsaGateway(http, () => device.CurrentToken), device);

        Assert.True(await device.RefreshAsync(EgressPurpose.Policy, TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromHours(6));
        Assert.Null(device.CurrentToken);

        var answer = await gateway.PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok, answer.Refusal);
        Assert.Equal(2, far.TokenRequests);
        Assert.Equal(1, far.PublishRequests);
        Assert.Equal("Bearer access-2", far.PublishAuthorization);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public async Task WithoutTheSignInThePublishAfterALongIdleWouldHaveBeenRefused()
    {
        // The mirror, so the test above is about the sign-in and not about a token that never ran out.
        var policy = Policy(localOnly: true, enforced: true);
        var far = new TokenBackend();
        using var http = new HttpClient(new EgressGuard(policy, far)) { BaseAddress = Backend };
        var time = new ManualTime(T0);
        var device = new DeviceSession(http, Activated(), time: time);
        Assert.True(await device.RefreshAsync(EgressPurpose.Policy, TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromHours(6));

        var answer = await new PsaGateway(http, () => device.CurrentToken).PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal(0, far.PublishRequests);
    }

    [Fact]
    public async Task ASignInThatFailsLeavesTheGatewaysOwnRefusal()
    {
        // The backend does not answer the sign-in: the publish is refused in the words it always used.
        var far = new TokenBackend { TokenStatus = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(new EgressGuard(Policy(localOnly: true, enforced: true), far)) { BaseAddress = Backend };
        var device = new DeviceSession(http, Activated(), time: new ManualTime(T0));
        var gateway = SigningInPsaGateway.For(new PsaGateway(http, () => device.CurrentToken), device);

        var answer = await gateway.PublishAsync(Wire(), TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Contains("not enrolled", answer.Refusal, StringComparison.Ordinal);
        Assert.Equal(0, far.PublishRequests);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 4)]
    public async Task TheRenewalLoopSignsInOnceForThePolicyAndThenOnlyWhenTheBackgroundMayRenew(bool background, int expected)
    {
        // Under local-only (background false) the loop signs in once, for the policy fetch waiting on the
        // first token, and then sends nothing on its timer. Otherwise it renews at every turn as before.
        var ct = TestContext.Current.CancellationToken;
        var policy = Policy(localOnly: !background, enforced: true);
        var far = new TokenBackend();
        var guard = new EgressGuard(policy, far);
        using var http = new HttpClient(guard) { BaseAddress = Backend };
        var device = new DeviceSession(http, Activated(), time: new ManualTime(T0));
        var tokenReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = new Turns(3);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var renewing = StartupOrder.RenewDeviceAsync(
            device,
            tokenReady,
            turns,
            stopping.Token,
            inBackground: () => policy.Decide(EgressPurpose.DeviceAuth, Backend).Allowed);
        await turns.Parked.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(tokenReady.Task.IsCompleted);
        Assert.Equal(expected, far.TokenRequests);
        Assert.Equal(0, guard.Blocked);

        await stopping.CancelAsync();
        await renewing.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task ThePolicyLoopSignsInBeforeEachFetch()
    {
        // The token the first fetch used has run out by the hourly one, and under local-only nothing
        // renewed it. The loop asks for a fresh one first, on behalf of the policy.
        var ct = TestContext.Current.CancellationToken;
        var far = new TokenBackend();
        using var http = new HttpClient(far) { BaseAddress = Backend };
        string? token = null;
        var signIns = 0;
        var policy = new PolicySync(http, () => token, new NoCache());
        var tokenReady = new TaskCompletionSource();
        tokenReady.SetResult();
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var syncing = StartupOrder.SyncPolicyAsync(
            policy,
            tokenReady.Task,
            fetched,
            TimeProvider.System,
            stopping.Token,
            signIn: _ =>
            {
                signIns++;
                token = "fresh-token";
                return Task.CompletedTask;
            });
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(1, signIns);
        Assert.Equal("Bearer fresh-token", far.PolicyAuthorization);

        await stopping.CancelAsync();
        await syncing.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private static EgressPolicy Policy(bool localOnly, bool enforced = false)
    {
        var policy = new EgressPolicy(ClientEgress.Settings(Backend));
        policy.Apply(localOnly, enforced);
        return policy;
    }

    private static DeviceSessionTests.InMemoryCredentials Activated() =>
        new(new DeviceCredential("refresh-1", "Acme IT", Guid.NewGuid(), T0));

    private static PublishWire Wire() => new()
    {
        SessionId = "s1",
        TicketId = "48213",
        NoteType = "internal",
        Minutes = 30,
        Destinations = ["ticket_note"],
        Note = new DraftNote
        {
            Problem = "Nothing printed.",
            Steps = [],
            Result = "It prints.",
            FollowUps = [],
            SuggestedTitle = "Printer offline",
            SuggestedTimeMinutes = 10,
            KbCandidate = false,
            KbReason = "n",
            Source = DraftSource.Cloud,
            PromptVersion = "note_v2",
        },
        Frames = [],
    };

    /// <summary>The backend's sign-in, publish and policy endpoints, counted.</summary>
    private sealed class TokenBackend : HttpMessageHandler
    {
        private int _tokens;
        private int _activations;
        private int _publishes;

        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;

        public int TokenRequests => Volatile.Read(ref _tokens);

        public int ActivateRequests => Volatile.Read(ref _activations);

        public int PublishRequests => Volatile.Read(ref _publishes);

        public string? PublishAuthorization { get; private set; }

        public string? PolicyAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/v1/devices/token":
                    var n = Interlocked.Increment(ref _tokens);
                    return Task.FromResult(TokenStatus == HttpStatusCode.OK
                        ? Json(new { accessToken = $"access-{n}", expiresAt = T0.AddHours(n == 1 ? 1 : 10) })
                        : new HttpResponseMessage(TokenStatus));

                case "/v1/devices/activate":
                    _ = Interlocked.Increment(ref _activations);
                    return Task.FromResult(Json(new { tenantName = "Acme IT", deviceId = Guid.NewGuid(), refreshToken = "refresh-1", accessToken = "access-0", expiresAt = T0.AddHours(1) }));

                case "/v1/sessions/publish":
                    _ = Interlocked.Increment(ref _publishes);
                    PublishAuthorization = request.Headers.Authorization?.ToString();
                    return Task.FromResult(Json(new { results = Array.Empty<object>() }));

                case "/v1/policy":
                    PolicyAuthorization = request.Headers.Authorization?.ToString();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"version":"v9","retentionDays":7,"localOnly":true,"localOnlyLocked":true,"captureAllWindows":false}""",
                            Encoding.UTF8,
                            "application/json"),
                    });

                default:
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private sealed class NoCache : IPolicyCache
    {
        public TenantPolicy? Load() => null;

        public void Save(TenantPolicy policy)
        {
        }
    }

    /// <summary>
    /// A clock whose timers fire at once for the first <c>turns</c> waits and never after, so a loop runs
    /// that many turns and then parks, which <see cref="Parked"/> says.
    /// </summary>
    private sealed class Turns(int turns) : TimeProvider
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _left = turns;

        public Task Parked => _parked.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Decrement(ref _left) >= 0)
            {
                _ = ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            else
            {
                _ = _parked.TrySetResult();
            }

            return new Never();
        }

        private sealed class Never : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
