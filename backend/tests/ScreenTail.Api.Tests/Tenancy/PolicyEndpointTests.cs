using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        _ = await api.UseAsync(db => Policies.SetAsync(db, tenantId, retentionDays: 14, localOnly: false, localOnlyLocked: false, captureAllWindows: false, TimeProvider.System));
        var latest = await api.UseAsync(db => Policies.SetAsync(db, tenantId, retentionDays: 3, localOnly: true, localOnlyLocked: true, captureAllWindows: false, TimeProvider.System));
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var theirs = Client(api.Issuer.ForDevice(otherTenant, otherUser, otherDevice).Token);

        var policy = await client.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken);
        var other = await theirs.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(latest.Version, policy!.Version);
        Assert.Equal(3, policy.RetentionDays);
        Assert.True(policy.LocalOnlyLocked);
        Assert.Equal("default", other!.Version);
    }

    private HttpClient Client(string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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
