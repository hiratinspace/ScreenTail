using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Net;
using ScreenTail.Core.Store;

namespace ScreenTail.Tests.Net;

/// <summary>
/// ST-047: the tenant's policy, fetched at start and hourly, applied to the guard, the retention job
/// and the scope, and kept so the last synced policy stands when the backend does not answer (AC2).
/// An enforced field is the admin's decision, not the technician's (INV-11); the client enforces it
/// rather than only displaying it.
/// </summary>
public sealed class PolicySyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheTenantsPolicyIsFetchedAppliedAndCached()
    {
        var backend = new OneReply(Json(new { version = "p20260925", retentionDays = 3, localOnly = true, localOnlyLocked = true, captureAllWindows = false }));
        var cache = new FilePolicyCache(Path.Combine(_dir, "policy.json"));
        var sync = new PolicySync(Client(backend), () => "token", cache);
        var changed = new List<TenantPolicy>();
        sync.Changed += changed.Add;

        Assert.True(await sync.FetchAsync(TestContext.Current.CancellationToken));

        Assert.Equal("p20260925", sync.Current.Version);
        Assert.Equal(3, sync.Current.RetentionDays);
        Assert.True(sync.Current.LocalOnlyLocked);
        Assert.Equal(sync.Current, cache.Load());
        Assert.Equal(sync.Current, Assert.Single(changed));
        Assert.EndsWith("/v1/policy", backend.Request!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("token", backend.Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task WhenTheBackendDoesNotAnswerTheLastSyncedPolicyStands()
    {
        var backend = new OneReply(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var cache = new FilePolicyCache(Path.Combine(_dir, "policy.json"));
        cache.Save(new TenantPolicy("p1", 3, true, true, false));
        var sync = new PolicySync(Client(backend), () => "token", cache);
        var changed = 0;
        sync.Changed += _ => changed++;

        Assert.False(await sync.FetchAsync(TestContext.Current.CancellationToken));

        Assert.Equal("p1", sync.Current.Version);
        Assert.Equal(3, sync.Current.RetentionDays);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void WithNothingSyncedTheDefaultApplies()
    {
        var sync = new PolicySync(Client(new OneReply(new HttpResponseMessage(HttpStatusCode.NotFound))), () => null, new FilePolicyCache(Path.Combine(_dir, "policy.json")));

        Assert.Equal(TenantPolicy.Default, sync.Current);
        Assert.Equal("default", sync.Current.Version);
        Assert.Equal(7, sync.Current.RetentionDays);
        Assert.False(sync.Current.LocalOnlyLocked);
    }

    [Fact]
    public void PolicyTrafficIsAllowedInLocalOnlyModeAndDraftingStillIsNot()
    {
        // The policy that enforces local-only has to be able to arrive, and to be lifted, while it is on.
        // It carries no content (INV-10) and goes only to the configured backend host.
        var policy = new EgressPolicy(new EgressSettings { LocalOnly = true, BackendHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.screentail.example" } });

        Assert.True(policy.Decide(EgressPurpose.Policy, new Uri("https://api.screentail.example/v1/policy")).Allowed);
        Assert.False(policy.Decide(EgressPurpose.Policy, new Uri("https://elsewhere.example/v1/policy")).Allowed);
        Assert.False(policy.Decide(EgressPurpose.Summarisation, new Uri("https://api.screentail.example/v1/sessions/summarize")).Allowed);
    }

    [Theory]
    [InlineData(3, true, true, false, true, true, 3)]
    [InlineData(3, false, true, true, false, true, 3)]
    [InlineData(14, false, false, true, true, false, 14)]
    [InlineData(14, true, false, false, false, false, 14)]
    public void TheAdminsPolicyIsAppliedAndALockedFieldOverridesTheTechnician(int retentionDays, bool localOnly, bool locked, bool userLocalOnly, bool expectedLocalOnly, bool expectedEnforced, int expectedDays)
    {
        // AC1: admin retention 3 d → client retention 3 d, field locked. Local-only follows the admin only
        // when locked; otherwise it is the technician's own switch.
        var applied = PolicyApplication.Resolve(new TenantPolicy("p1", retentionDays, localOnly, locked, false), userLocalOnly);

        Assert.Equal(TimeSpan.FromDays(expectedDays), applied.Retention);
        Assert.Equal(expectedLocalOnly, applied.LocalOnly);
        Assert.Equal(expectedEnforced, applied.Enforced);
        Assert.Equal("p1", applied.Version);
    }

    [Fact]
    public void ApplyingAPolicyChangesTheGuardTheRetentionAndTheScopeInPlace()
    {
        var egress = new EgressPolicy(new EgressSettings { BackendHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.screentail.example" } });
        var retention = new RetentionOptions();
        var scope = new ScopePolicy(RemoteToolRegistry.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json"))));

        egress.Apply(localOnly: true, enforced: true);
        retention.Retention = TimeSpan.FromDays(3);
        scope.Apply(new ScopeOptions { CaptureAllWindows = true });

        Assert.True(egress.Settings.LocalOnly);
        Assert.False(egress.CanChangeLocally);
        Assert.Equal("Local-only (set by your administrator)", egress.Badge);
        Assert.False(egress.Decide(EgressPurpose.Backend, new Uri("https://api.screentail.example/x")).Allowed);
        Assert.Equal(TimeSpan.FromDays(3), retention.Retention);
        Assert.True(scope.Options.CaptureAllWindows);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.screentail.example/") };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class OneReply(HttpResponseMessage reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(reply);
        }
    }
}
