using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// Who may call what (ST-099, ADR-0009). A device's token never opens an admin route, an admin session
/// never acts as a device, a change needs the CSRF header, and an owner who is disabled is turned away
/// like a forged session. The admin routes are found from the running app, like
/// <c>EveryRouteRevocationTests</c> finds the device ones, so a route added later is covered the day it
/// is written.
/// </summary>
public sealed partial class AdminAccessTests(AdminApiFixture api) : IClassFixture<AdminApiFixture>
{
    [Fact]
    public async Task ADevicesTokenOpensNoAdminRoute()
    {
        var owner = await api.SeedOwnerAsync();
        var (_, _, device) = await api.SeedDeviceAsync(owner.TenantId);
        using var client = api.Device(device);

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Fact]
    public async Task ADevicesTokenInTheAdminCookieOpensNoAdminRoute()
    {
        var owner = await api.SeedOwnerAsync();
        var (_, _, device) = await api.SeedDeviceAsync(owner.TenantId);
        using var client = api.Owner(owner with { Token = device });

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Fact]
    public async Task ATokenWithAnOwnersClaimsButADevicesAudienceOpensNoAdminRoute()
    {
        // Everything an owner's session carries, for an owner who exists, signed with the right key, but
        // minted for the devices' audience. Only the admin scheme's audience check stands in its way.
        var owner = await api.SeedOwnerAsync();
        var forged = Sign(api.Jwt.Audience, [
            new(ScreenTailClaims.TenantId, owner.TenantId.ToString()),
            new(ScreenTailClaims.OwnerId, owner.OwnerId.ToString()),
            new(ScreenTailClaims.Role, AdminSession.OwnerRole),
            new(ScreenTailClaims.Csrf, owner.Csrf),
            new(ScreenTailClaims.SessionVersion, "0"),
        ]);
        using var client = api.Owner(owner with { Token = forged });

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Theory]
    [InlineData("no role")]
    [InlineData("another role")]
    [InlineData("a device id")]
    public async Task AnAdminAudienceTokenThatIsNotAnOwnersSessionOpensNoAdminRoute(string flaw)
    {
        // The admin audience and an owner who exists, but not an owner's session: no role, a role that
        // is not the owner's, or a device id beside the owner's (a device's token, whatever else it says).
        var owner = await api.SeedOwnerAsync();
        List<Claim> claims = [
            new(ScreenTailClaims.TenantId, owner.TenantId.ToString()),
            new(ScreenTailClaims.OwnerId, owner.OwnerId.ToString()),
            new(ScreenTailClaims.Csrf, owner.Csrf),
            new(ScreenTailClaims.SessionVersion, "0"),
        ];
        switch (flaw)
        {
            case "another role":
                claims.Add(new(ScreenTailClaims.Role, "technician"));
                break;
            case "a device id":
                claims.Add(new(ScreenTailClaims.Role, AdminSession.OwnerRole));
                claims.Add(new(ScreenTailClaims.DeviceId, Guid.NewGuid().ToString()));
                break;
            default:
                break;
        }

        using var client = api.Owner(owner with { Token = Sign(AdminSession.Audience, [.. claims]) });

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Fact]
    public async Task TheAdminSessionIsReadFromTheCookieAndNeverFromTheAuthorizationHeader()
    {
        // A good owner's session in the header, where a device's token travels, opens nothing. The
        // header is the device scheme's; the admin scheme reads its cookie and nothing else.
        var owner = await api.SeedOwnerAsync();
        using var client = api.Device(owner.Token);
        client.DefaultRequestHeaders.Add(AdminSession.CsrfHeader, owner.Csrf);

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Fact]
    public async Task AnAdminSessionActsAsNoDevice()
    {
        var owner = await api.SeedOwnerAsync();
        using var asCookie = api.Owner(owner);
        using var asBearer = api.Device(owner.Token);

        Assert.Empty(await AnsweredOtherThan(asCookie, HttpStatusCode.Unauthorized, DeviceRoutes()));
        Assert.Empty(await AnsweredOtherThan(asBearer, HttpStatusCode.Unauthorized, DeviceRoutes()));
    }

    [Fact]
    public async Task ATokenWithADevicesClaimsButTheAdminAudienceActsAsNoDevice()
    {
        // A real, active device's claims, signed with the right key, minted for the admin audience. Only
        // the device scheme's audience check stands in its way.
        var owner = await api.SeedOwnerAsync();
        var (userId, deviceId, _) = await api.SeedDeviceAsync(owner.TenantId);
        var forged = Sign(AdminSession.Audience, [
            new(ScreenTailClaims.TenantId, owner.TenantId.ToString()),
            new(ScreenTailClaims.UserId, userId.ToString()),
            new(ScreenTailClaims.DeviceId, deviceId.ToString()),
        ]);
        using var client = api.Device(forged);

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, DeviceRoutes()));
    }

    [Fact]
    public async Task AGoodOwnerReachesEveryAdminRouteHandler()
    {
        // The control for the refusals above: an enumeration that found nothing, or routes that refused
        // everybody, would pass them. Every route answers with its handler's own result.
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);
        var refused = new List<string>();

        foreach (var (method, path) in OwnerRoutes())
        {
            using var request = Request(method, path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                || ((int)response.StatusCode >= 400 && response.Content.Headers.ContentType?.MediaType != "application/json"))
            {
                refused.Add($"{method} {path} answered {(int)response.StatusCode}");
            }
        }

        Assert.Empty(refused);
        Assert.Contains(OwnerRoutes(), r => r == ("PUT", "/v1/admin/policy"));
        Assert.Contains(OwnerRoutes(), r => r == ("POST", "/v1/admin/invites"));
    }

    [Fact]
    public async Task EveryChangeNeedsTheSessionsCsrfValue()
    {
        var owner = await api.SeedOwnerAsync();
        using var without = api.Owner(owner, csrf: false);
        using var wrong = api.Owner(owner with { Csrf = AdminSession.NewCsrf() });
        var changes = OwnerRoutes().Where(r => r.Method != "GET").ToList();

        Assert.NotEmpty(changes);
        Assert.Empty(await AnsweredOtherThan(without, HttpStatusCode.Forbidden, changes));
        Assert.Empty(await AnsweredOtherThan(wrong, HttpStatusCode.Forbidden, changes));

        // A read needs none: it changes nothing, and the page asks for the value with one.
        using var read = await without.GetAsync(new Uri("/v1/admin/session", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0, await api.UseAsync(db => db.Invites.CountAsync(i => i.TenantId == owner.TenantId)));
        Assert.Equal(0, await api.UseAsync(db => db.Policies.CountAsync(p => p.TenantId == owner.TenantId)));
    }

    [Fact]
    public async Task ADisabledOwnerOrTenantIsTurnedAwayLikeAForgedSession()
    {
        var disabledOwner = await api.SeedOwnerAsync();
        _ = await api.UseAsync(db => db.Owners.Where(o => o.Id == disabledOwner.OwnerId).ExecuteUpdateAsync(s => s.SetProperty(o => o.DisabledAt, DateTimeOffset.UtcNow)));
        var disabledTenant = await api.SeedOwnerAsync();
        _ = await api.UseAsync(db => db.Tenants.Where(t => t.Id == disabledTenant.TenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DisabledAt, DateTimeOffset.UtcNow)));
        using var one = api.Owner(disabledOwner);
        using var two = api.Owner(disabledTenant);
        using var forged = api.Owner(disabledOwner with { Token = "not.a.token" });

        Assert.Empty(await AnsweredOtherThan(one, HttpStatusCode.Unauthorized, OwnerRoutes()));
        Assert.Empty(await AnsweredOtherThan(two, HttpStatusCode.Unauthorized, OwnerRoutes()));
        Assert.Empty(await AnsweredOtherThan(forged, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Fact]
    public async Task AnOwnersSessionForAnotherTenantIsNoSession()
    {
        // The owner of one tenant with the other tenant's id in the claim: the row must match both.
        var mine = await api.SeedOwnerAsync();
        var theirs = await api.SeedOwnerAsync();
        var crossed = Sign(AdminSession.Audience, [
            new(ScreenTailClaims.TenantId, theirs.TenantId.ToString()),
            new(ScreenTailClaims.OwnerId, mine.OwnerId.ToString()),
            new(ScreenTailClaims.Role, AdminSession.OwnerRole),
            new(ScreenTailClaims.Csrf, mine.Csrf),
            new(ScreenTailClaims.SessionVersion, "0"),
        ]);
        using var client = api.Owner(mine with { Token = crossed });

        Assert.Empty(await AnsweredOtherThan(client, HttpStatusCode.Unauthorized, OwnerRoutes()));
    }

    [Fact]
    public void TheDeviceRoutesAndTheAdminRoutesShareNoPolicy()
    {
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        foreach (var endpoint in endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/v1/admin/", StringComparison.Ordinal) == true))
        {
            Assert.DoesNotContain(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == CallerPolicy.Name);
        }

        foreach (var endpoint in endpoints.Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == CallerPolicy.Name)))
        {
            Assert.DoesNotContain(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == OwnerPolicy.Name);
        }
    }

    private string Sign(string audience, Claim[] claims)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = api.Jwt.Issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(api.Jwt.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<List<string>> AnsweredOtherThan(HttpClient client, HttpStatusCode expected, IEnumerable<(string Method, string Path)> routes)
    {
        var other = new List<string>();
        foreach (var (method, path) in routes)
        {
            using var request = Request(method, path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode != expected)
            {
                other.Add($"{method} {path} answered {(int)response.StatusCode}");
            }
        }

        return other;
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    /// <summary>Every route behind the owner policy, each parameter filled with a fresh id.</summary>
    private List<(string Method, string Path)> OwnerRoutes() => Routes(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == OwnerPolicy.Name), Guid.Empty.ToString());

    /// <summary>Every route behind the device policy.</summary>
    private List<(string Method, string Path)> DeviceRoutes() => Routes(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == CallerPolicy.Name), "connectwise");

    private List<(string Method, string Path)> Routes(Func<RouteEndpoint, bool> which, string parameter)
    {
        var routes = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null && which(e))
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => (method, RouteParameter().Replace(e.RoutePattern.RawText!, parameter))))
            .Distinct()
            .OrderBy(r => r.Item2, StringComparer.Ordinal)
            .ThenBy(r => r.method, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(routes);
        return routes;
    }

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }
}
