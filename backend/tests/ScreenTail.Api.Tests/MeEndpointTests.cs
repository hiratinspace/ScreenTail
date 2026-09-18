using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests;

/// <summary>
/// ST-008's first criterion, and the test of the whole authentication chain.
///
/// Every way a request can fail to be trustworthy is here, because this is the endpoint every client
/// calls at startup and the one an attacker would probe first. Each case returns 401 and says nothing
/// about which check failed: an error that explains itself is an error that helps whoever is guessing.
/// </summary>
public sealed class MeEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task AValidDeviceTokenGetsItsTenantTechnicianAndDevice()
    {
        var (tenantId, userId, deviceId) = await SeedAsync("Acme IT", "tech@acme.example");
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Acme IT", me!.Tenant.Name);
        Assert.Equal("tech@acme.example", me.User.Email);
        Assert.Equal(deviceId, me.Device.Id);
    }

    [Fact]
    public async Task NoTokenIsRefused()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AGarbageTokenIsRefused()
    {
        using var client = Client("not.a.token");

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKeyIsRefused()
    {
        // The whole point of signing. A token minted by anything but this deployment is worthless here,
        // however well-formed it looks.
        var (tenantId, userId, deviceId) = await SeedAsync("Forged", "forged@example.com");
        var impostor = new TokenIssuer(new JwtOptions
        {
            Issuer = api.Jwt.Issuer,
            Audience = api.Jwt.Audience,
            SigningKey = "a-different-key-that-is-also-long-enough!",
        });
        using var client = Client(impostor.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        // No clock skew is allowed, so "expired" means expired. The default five-minute grace is a long
        // time for a token somebody took the trouble to steal.
        var (tenantId, userId, deviceId) = await SeedAsync("Expired", "expired@example.com");
        var past = new FixedTime(DateTimeOffset.UtcNow - TimeSpan.FromDays(2));
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId, past).Token);

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARevokedDeviceIsRefusedEvenWithAValidToken()
    {
        // The case a signature check cannot catch. A token stays valid for its whole lifetime, so a
        // technician who leaves must stop working before it expires, not when it does.
        var (tenantId, userId, deviceId) = await SeedAsync("Leaver", "leaver@example.com");
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        _ = await api.UseAsync(db => db.Devices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));

        using var client = Client(token);
        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ADisabledTechnicianIsRefused()
    {
        var (tenantId, userId, deviceId) = await SeedAsync("Offboarded", "gone@example.com");
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        _ = await api.UseAsync(db => db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)));

        using var client = Client(token);
        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenCannotReachAnotherTenantsDevice()
    {
        // The failure that would matter most. Every lookup is scoped by the tenant in the token, so a
        // token that names the wrong tenant finds nothing rather than finding somebody else's row.
        var (_, _, deviceId) = await SeedAsync("Real", "real@example.com");
        var (otherTenant, otherUser, _) = await SeedAsync("Other", "other@example.com");
        using var client = Client(api.Issuer.ForDevice(otherTenant, otherUser, deviceId).Token);

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheRefusalSaysNothingAboutWhy()
    {
        // A 401 naming the failed check tells whoever is probing which half of their guess was right.
        using var client = Client("not.a.token");

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        var header = response.Headers.WwwAuthenticate.ToString();

        Assert.DoesNotContain("error_description", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskingUpdatesWhenTheDeviceWasLastSeen()
    {
        var (tenantId, userId, deviceId) = await SeedAsync("Seen", "seen@example.com");
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var lastSeen = await api.UseAsync(db => db.Devices
            .Where(d => d.Id == deviceId)
            .Select(d => d.LastSeenAt)
            .SingleAsync());

        Assert.NotNull(lastSeen);
    }

    private HttpClient Client(string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync(string tenantName, string email)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = tenantName, Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User
            {
                Id = userId,
                TenantId = tenantId,
                Email = email,
                DisplayName = "A Technician",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Devices.Add(new Device
            {
                Id = deviceId,
                TenantId = tenantId,
                UserId = userId,
                Name = "TECH-LAPTOP",
                TokenHash = DeviceTokens.Hash(DeviceTokens.Create()),
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        return (tenantId, userId, deviceId);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
