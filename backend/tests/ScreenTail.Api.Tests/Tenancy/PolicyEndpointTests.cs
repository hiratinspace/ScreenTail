using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Tenancy;

/// <summary>
/// ST-047's server side: <c>GET /v1/policy</c> answers a device with its tenant's latest policy, or the
/// defaults when the admin has set none; <c>Policies.SetAsync</c> is what the operator's flag calls.
/// </summary>
public sealed class PolicyEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task WithNoPolicySetTheDefaultsComeBack()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        var policy = await client.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("default", policy!.Version);
        Assert.Equal(7, policy.RetentionDays);
        Assert.False(policy.LocalOnly);
        Assert.False(policy.LocalOnlyLocked);
        Assert.False(policy.CaptureAllWindows);
    }

    [Fact]
    public async Task TheLatestPolicyIsTheOneADeviceGetsAndAnotherTenantsIsNot()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var (otherTenant, otherUser, otherDevice) = await SeedAsync();
        // A version is the time to the millisecond, so the two are set a minute apart rather than on the
        // wall clock, where a warm run can land both in the same millisecond.
        var now = DateTimeOffset.UtcNow;
        _ = await api.UseAsync(db => Policies.SetAsync(db, tenantId, retentionDays: 14, localOnly: false, localOnlyLocked: false, captureAllWindows: false, new At(now.AddMinutes(-1))));
        var latest = await api.UseAsync(db => Policies.SetAsync(db, tenantId, retentionDays: 3, localOnly: true, localOnlyLocked: true, captureAllWindows: false, new At(now)));
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var theirs = Client(api.Issuer.ForDevice(otherTenant, otherUser, otherDevice).Token);

        var policy = await client.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken);
        var other = await theirs.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(latest.Version, policy!.Version);
        Assert.Equal(3, policy.RetentionDays);
        Assert.True(policy.LocalOnlyLocked);
        Assert.Equal("default", other!.Version);
    }

    [Fact]
    public async Task ThePolicyResponseIsTheSharedContract()
    {
        // shared/contracts/tenant-policy.v1.json is what the client reads. A field renamed here would read
        // as false there, and a locked local-only would arrive unlocked (INV-11), so the names and the
        // types are compared for the defaults and for a policy the admin set.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var contract = JsonDocument.Parse(Contract("response"));

        using var defaults = JsonDocument.Parse(await client.GetStringAsync(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken));
        _ = await api.UseAsync(db => Policies.SetAsync(db, tenantId, retentionDays: 3, localOnly: true, localOnlyLocked: true, captureAllWindows: true, new Policies.UpdateSettings("pilot", ChannelLocked: true, CheckHours: 6, UnderLocalOnly: true), TimeProvider.System));
        using var set = JsonDocument.Parse(await client.GetStringAsync(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken));

        Assert.Equal(Shape(contract.RootElement), Shape(defaults.RootElement));
        Assert.Equal(Shape(contract.RootElement), Shape(set.RootElement));
        foreach (var field in contract.RootElement.EnumerateObject().Where(p => p.Name != "version"))
        {
            Assert.Equal(field.Value.GetRawText(), set.RootElement.GetProperty(field.Name).GetRawText());
        }
    }

    private static string Contract(string property)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.tenant-policy.v1.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(property).GetRawText();
    }

    private static string Shape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + Shape(p.Value))) + "}",
        JsonValueKind.Array => "[" + (element.GetArrayLength() > 0 ? Shape(element[0]) : string.Empty) + "]",
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => element.ValueKind.ToString().ToLowerInvariant(),
    };

    private HttpClient Client(string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class At(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }
}
