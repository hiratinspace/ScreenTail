using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tests.Auth;

/// <summary>
/// Revocation on every authenticated route, not only on the ones somebody remembered (ST-010; 2026-10-12
/// review, T-173).
///
/// The check used to be a hand-written call at the top of each of twelve handlers, and only two of them
/// had a test. A handler that lost the call would have gone on serving a revoked laptop for the rest of
/// its token's hour: publishing notes and time into the tenant's PSA, or replacing its PSA credential.
///
/// The routes come from the running app's endpoint source rather than from a list in this file, so the
/// next endpoint mapped under /v1 is covered the day it is written. /v1/devices is left out because it is
/// the anonymous door a device uses before it has a token.
/// </summary>
public sealed partial class EveryRouteRevocationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    /// <summary>Why a holder of a correctly signed token may still not act.</summary>
    public enum Standing
    {
        RevokedDevice,
        DisabledTechnician,
        DisabledTenant,
        DeviceNeverActivated,
        AnotherTenantsDevice,
    }

    [Theory]
    [InlineData(Standing.RevokedDevice)]
    [InlineData(Standing.DisabledTechnician)]
    [InlineData(Standing.DisabledTenant)]
    [InlineData(Standing.DeviceNeverActivated)]
    [InlineData(Standing.AnotherTenantsDevice)]
    public async Task EveryAuthenticatedRouteTurnsAwayARevokedDevice(Standing standing)
    {
        // An empty JSON body on every route that takes one. On the old code a revoked device sending it
        // to publish or summarize got the binder's 400 back, because the shape was read before anybody
        // asked who was sending it.
        using var client = Client(await TokenForAsync(standing));
        var answered = new List<string>();

        foreach (var (method, path) in AuthenticatedRoutes())
        {
            using var request = Request(method, path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                answered.Add($"{method} {path} answered {(int)response.StatusCode}");
            }
        }

        Assert.Empty(answered);
    }

    [Fact]
    public async Task EveryAuthenticatedRouteLetsAGoodDeviceThrough()
    {
        // The control. Without it a route that refused everybody would pass the theory above, and so
        // would an enumeration that found nothing.
        using var client = Client(await TokenForAsync(standing: null));
        var refused = new List<string>();

        foreach (var (method, path) in AuthenticatedRoutes())
        {
            using var request = Request(method, path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                refused.Add($"{method} {path} answered {(int)response.StatusCode}");
            }
        }

        Assert.Empty(refused);
    }

    [Fact]
    public void TheEnumerationFindsTheRoutesThatMatterMost()
    {
        // The two the review named, so a change to how routes are found cannot quietly shrink the list.
        var routes = AuthenticatedRoutes().Select(r => $"{r.Method} {r.Path}").ToList();

        Assert.Contains("POST /v1/sessions/publish", routes);
        Assert.Contains("PUT /v1/integrations/connectwise", routes);
        Assert.Contains("POST /v1/sessions/summarize", routes);
        Assert.Contains("GET /v1/me", routes);
        Assert.DoesNotContain(routes, r => r.Contains("/v1/devices/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Standing.RevokedDevice)]
    [InlineData(Standing.DisabledTenant)]
    public async Task ARefusedDeviceIsToldNoMoreThanAForgedToken(Standing standing)
    {
        // A 401 that differs from a bad token's tells whoever holds a stolen one that it was once good,
        // and that what changed is on our side. Same status, same challenge, no body.
        using var forged = Client("not.a.token");
        using var refused = Client(await TokenForAsync(standing));
        var different = new List<string>();

        foreach (var (method, path) in AuthenticatedRoutes())
        {
            using var forgedRequest = Request(method, path);
            using var refusedRequest = Request(method, path);
            using var expected = await forged.SendAsync(forgedRequest, TestContext.Current.CancellationToken);
            using var actual = await refused.SendAsync(refusedRequest, TestContext.Current.CancellationToken);
            var expectedBody = await expected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var actualBody = await actual.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            if (actual.StatusCode != expected.StatusCode
                || actual.Headers.WwwAuthenticate.ToString() != expected.Headers.WwwAuthenticate.ToString()
                || actualBody != expectedBody)
            {
                different.Add($"{method} {path}: {(int)actual.StatusCode} '{actual.Headers.WwwAuthenticate}' {actualBody.Length} bytes");
            }
        }

        Assert.Empty(different);
    }

    [Fact]
    public async Task ACallerAskedForOutsideThePolicyIsStillChecked()
    {
        // A handler takes a Caller parameter. Behind the policy it gets the one the policy proved; on a
        // route somebody maps outside the group it is checked there and then, and a caller who may not
        // act binds to nothing, so the handler never runs.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using (var scope = api.Services.CreateScope())
        {
            Assert.Equal(deviceId, (await Caller.BindAsync(Request(scope, tenantId, userId, deviceId)))?.DeviceId);
        }

        _ = await api.UseAsync(db => db.Devices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));

        using (var scope = api.Services.CreateScope())
        {
            Assert.Null(await Caller.BindAsync(Request(scope, tenantId, userId, deviceId)));
            Assert.Null(await Caller.BindAsync(new DefaultHttpContext { RequestServices = scope.ServiceProvider }));
        }
    }

    private static DefaultHttpContext Request(IServiceScope scope, Guid tenantId, Guid userId, Guid deviceId) => new()
    {
        RequestServices = scope.ServiceProvider,
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ScreenTailClaims.TenantId, tenantId.ToString()),
                new Claim(ScreenTailClaims.UserId, userId.ToString()),
                new Claim(ScreenTailClaims.DeviceId, deviceId.ToString()),
            ],
            authenticationType: "Bearer")),
    };

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    /// <summary>
    /// Every method and path under /v1 except the anonymous device door, with each route parameter filled
    /// by a value that is valid wherever it appears (a provider name, a company name).
    /// </summary>
    private List<(string Method, string Path)> AuthenticatedRoutes() =>
    [
        .. api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw
                && raw.StartsWith("/v1/", StringComparison.Ordinal)
                && !raw.StartsWith("/v1/devices/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (method, RouteParameter().Replace(e.RoutePattern.RawText!, "connectwise"))))
            .Distinct()
            .OrderBy(r => r.Item2, StringComparer.Ordinal)
            .ThenBy(r => r.method, StringComparer.Ordinal),
    ];

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private HttpClient Client(string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A correctly signed token for a device in the given standing, or a good one for null.</summary>
    private async Task<string> TokenForAsync(Standing? standing)
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        switch (standing)
        {
            case Standing.RevokedDevice:
                _ = await api.UseAsync(db => db.Devices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));
                break;
            case Standing.DisabledTechnician:
                _ = await api.UseAsync(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(set => set.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)));
                break;
            case Standing.DisabledTenant:
                _ = await api.UseAsync(db => db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(set => set.SetProperty(t => t.DisabledAt, DateTimeOffset.UtcNow)));
                break;
            case Standing.DeviceNeverActivated:
                deviceId = Guid.NewGuid();
                break;
            case Standing.AnotherTenantsDevice:
                var (otherTenant, otherUser, _) = await SeedAsync();
                return api.Issuer.ForDevice(otherTenant, otherUser, deviceId).Token;
            case null:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(standing), standing, null);
        }

        return api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
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
