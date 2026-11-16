using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests;

/// <summary>
/// The owner's integration routes, for tests that need a credential stored or removed (2026-11-15
/// review): only the tenant's owner may do that now, from the web admin, so a test does what the owner
/// would, with a session cookie and the CSRF header, against whichever host it runs.
/// </summary>
internal static class OwnerRoutes
{
    public const string OwnerName = "Test Owner";

    /// <summary>A client signed in as the tenant's owner, made if the tenant has none yet.</summary>
    public static async Task<HttpClient> OwnerClientAsync(this WebApplicationFactory<Program> host, ApiFixture api, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(api);
        var owner = await OwnerAsync(api, tenantId);
        var csrf = AdminSession.NewCsrf();
        var (token, _) = api.Issuer.ForOwner(tenantId, owner.Id, owner.SessionVersion, csrf, TimeSpan.FromHours(1));
        var client = host.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminSession.CookieName}={token}");
        client.DefaultRequestHeaders.Add(AdminSession.CsrfHeader, csrf);
        return client;
    }

    /// <summary>The tenant's owner, made with <see cref="OwnerName"/> if the tenant has none yet.</summary>
    public static Task<Owner> OwnerAsync(ApiFixture api, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.UseAsync(async db =>
        {
            if (await db.Owners.AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == tenantId) is { } existing)
            {
                return existing;
            }

            var made = new Owner
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                GoogleSubject = $"test-{Guid.NewGuid():N}",
                Email = $"{Guid.NewGuid():N}@owner.example",
                DisplayName = OwnerName,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Owners.Add(made);
            _ = await db.SaveChangesAsync();
            return made;
        });
    }

    /// <summary><c>PUT /v1/admin/integrations/{provider}</c> as the tenant's owner.</summary>
    public static async Task<HttpResponseMessage> StoreIntegrationAsync(this WebApplicationFactory<Program> host, ApiFixture api, Guid tenantId, string provider, StoreIntegrationRequest request)
    {
        using var client = await host.OwnerClientAsync(api, tenantId);
        return await client.PutAsJsonAsync(new Uri($"/v1/admin/integrations/{provider}", UriKind.Relative), request, TestContext.Current.CancellationToken);
    }

    /// <summary><c>DELETE /v1/admin/integrations/{provider}</c> as the tenant's owner.</summary>
    public static async Task<HttpResponseMessage> RemoveIntegrationAsync(this WebApplicationFactory<Program> host, ApiFixture api, Guid tenantId, string provider)
    {
        using var client = await host.OwnerClientAsync(api, tenantId);
        return await client.DeleteAsync(new Uri($"/v1/admin/integrations/{provider}", UriKind.Relative), TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> StoreIntegrationAsync(this ApiFixture api, Guid tenantId, string provider, StoreIntegrationRequest request) =>
        StoreIntegrationAsync(api, api, tenantId, provider, request);

    public static Task<HttpResponseMessage> RemoveIntegrationAsync(this ApiFixture api, Guid tenantId, string provider) =>
        RemoveIntegrationAsync(api, api, tenantId, provider);

    /// <summary>
    /// <c>PUT /v1/admin/integrations/{provider}</c> as the owner of the tenant a device client belongs to,
    /// sent through that client so it reaches the same host. The owner's cookie is what the admin routes
    /// read; the device's bearer header that comes along is ignored by them.
    /// </summary>
    public static async Task<HttpResponseMessage> StoreIntegrationAsOwnerAsync(this HttpClient device, ApiFixture api, string provider, StoreIntegrationRequest request)
    {
        using var message = await AsOwnerAsync(device, api, HttpMethod.Put, provider);
        message.Content = JsonContent.Create(request);
        return await device.SendAsync(message, TestContext.Current.CancellationToken);
    }

    /// <summary><c>DELETE /v1/admin/integrations/{provider}</c>, the same way.</summary>
    public static async Task<HttpResponseMessage> RemoveIntegrationAsOwnerAsync(this HttpClient device, ApiFixture api, string provider)
    {
        using var message = await AsOwnerAsync(device, api, HttpMethod.Delete, provider);
        return await device.SendAsync(message, TestContext.Current.CancellationToken);
    }

    /// <summary>The tenant a device client's bearer token is for.</summary>
    public static Guid TenantOf(HttpClient device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var bearer = device.DefaultRequestHeaders.Authorization?.Parameter
            ?? throw new InvalidOperationException("The client carries no device token to read the tenant from.");
        return Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(bearer).GetClaim(ScreenTailClaims.TenantId).Value);
    }

    private static async Task<HttpRequestMessage> AsOwnerAsync(HttpClient device, ApiFixture api, HttpMethod method, string provider)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(api);
        var tenantId = TenantOf(device);
        var owner = await OwnerAsync(api, tenantId);
        var csrf = AdminSession.NewCsrf();
        var (token, _) = api.Issuer.ForOwner(tenantId, owner.Id, owner.SessionVersion, csrf, TimeSpan.FromHours(1));
        var message = new HttpRequestMessage(method, new Uri($"/v1/admin/integrations/{provider}", UriKind.Relative));
        message.Headers.Add("Cookie", $"{AdminSession.CookieName}={token}");
        message.Headers.Add(AdminSession.CsrfHeader, csrf);
        return message;
    }
}
