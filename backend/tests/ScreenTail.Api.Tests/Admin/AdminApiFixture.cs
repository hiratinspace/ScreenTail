using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// The API with owner sign-in switched on (ST-099): this suite's Google client id, a signup list, three
/// seats for a new tenant, and <see cref="FakeGoogleKeys"/> in place of Google's published keys.
/// </summary>
public class AdminApiFixture : ApiFixture
{
    /// <summary>Addresses on the signup list. Each test that signs up takes its own, so tests never share a tenant.</summary>
    public static readonly IReadOnlyList<string> Allowed = [.. Enumerable.Range(0, 40).Select(i => $"owner{i}@acme.example")];

    public const int NewTenantSeats = 3;

    public AdminApiFixture()
    {
        Keys = new FakeGoogleKeys(Google.Key);
    }

    internal GoogleTokens Google { get; } = new();

    internal FakeGoogleKeys Keys { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.UseSetting("Admin:GoogleClientId", GoogleTokens.ClientId);
        for (var i = 0; i < Allowed.Count; i++)
        {
            builder.UseSetting($"Admin:SignupAllowlist:{i}", Allowed[i]);
        }

        builder.UseSetting("Admin:NewTenantSeats", NewTenantSeats.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureServices(services =>
        {
            _ = services.RemoveAll<IGoogleSigningKeys>();
            _ = services.AddSingleton<IGoogleSigningKeys>(Keys);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Google.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A tenant with an owner, made directly, and a session for that owner.</summary>
    public async Task<OwnerSession> SeedOwnerAsync(string tenantName = "Acme IT", int seats = 5)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = tenantName, Seats = seats, CreatedAt = DateTimeOffset.UtcNow };
        var owner = new Owner
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            GoogleSubject = $"seeded-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@acme.example",
            DisplayName = "Seeded Owner",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _ = await UseAsync(async db =>
        {
            db.Tenants.Add(tenant);
            db.Owners.Add(owner);
            return await db.SaveChangesAsync();
        });
        var csrf = AdminSession.NewCsrf();
        var (token, _) = Issuer.ForOwner(tenant.Id, owner.Id, csrf, TimeSpan.FromHours(1));
        return new OwnerSession(tenant.Id, owner.Id, token, csrf);
    }

    /// <summary>A technician with one active device in the tenant, and a token for that device.</summary>
    public async Task<(Guid UserId, Guid DeviceId, string Token)> SeedDeviceAsync(Guid tenantId, string deviceName = "TECH-LAPTOP")
    {
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await UseAsync(async db =>
        {
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = deviceName, TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (userId, deviceId, Issuer.ForDevice(tenantId, userId, deviceId).Token);
    }

    /// <summary>A client that sends the session cookie, and the CSRF header unless told not to.</summary>
    public HttpClient Owner(OwnerSession session, bool csrf = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        var client = CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSession.CookieName}={session.Token}");
        if (csrf)
        {
            client.DefaultRequestHeaders.Add(AdminSession.CsrfHeader, session.Csrf);
        }

        return client;
    }

    /// <summary>A client that sends a device's bearer token and nothing else.</summary>
    public HttpClient Device(string token)
    {
        var client = CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <param name="Token">The signed session, as the cookie carries it.</param>
public sealed record OwnerSession(Guid TenantId, Guid OwnerId, string Token, string Csrf);
