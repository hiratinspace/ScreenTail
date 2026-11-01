using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// Every answer of the API says it is not to be rendered, framed, sniffed or followed by a referrer
/// (ST-099 hardening, ADR-0009): the admin's routes, the devices' routes, refusals, a body refused
/// before it is read, and a path that does not exist.
/// </summary>
public sealed class SecurityHeadersTests(AdminApiFixture api) : IClassFixture<AdminApiFixture>
{
    public static TheoryData<string, string> Answers => new()
    {
        { "GET", "/health" },
        { "GET", "/v1/admin/session" },
        { "POST", "/v1/admin/session" },
        { "POST", "/v1/admin/session/nonce" },
        { "POST", "/v1/admin/session/end" },
        { "POST", "/v1/devices/activate" },
        { "GET", "/v1/me" },
        { "GET", "/v1/admin/technicians" },
        { "GET", "/no/such/path" },
    };

    private static void AssertHeaders(HttpResponseMessage response)
    {
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
        Assert.Contains("camera=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task EveryAnswerCarriesTheHeaders(string method, string path)
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        AssertHeaders(response);
    }

    [Fact]
    public async Task ABodyRefusedBeforeItIsReadCarriesThemToo()
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        using var content = new ByteArrayContent(new byte[(int)ScreenTail.Api.Auth.AdminSession.AnonymousBodyLimit + 1]);
        content.Headers.ContentType = new("application/json");

        using var response = await client.PostAsync(new Uri("/v1/admin/session", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertHeaders(response);
    }

    [Fact]
    public async Task AnUnhandledErrorOutsideDevelopmentIsABare500WithThemToo()
    {
        // Production, where no developer exception page stands between the exception and the server: the
        // database fails as a signup saves, nothing catches it, and the 500 still carries the headers and
        // says nothing about what went wrong.
        using var host = api.WithWebHostBuilder(b =>
        {
            _ = b.UseEnvironment(Environments.Production);
            _ = b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(new FailOwnerInserts())));
        });
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/admin/signup", UriKind.Relative),
            new AdminSignupRequest(api.Google.Mint(subject: "sub-headers-500", email: AdminApiFixture.Allowed[25]), "Broken Headers Co"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertHeaders(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("database went away", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("credential=x", "application/x-www-form-urlencoded", HttpStatusCode.UnsupportedMediaType)]
    public async Task ABodyThatCannotBeBoundInDevelopmentIsTheCallersMistakeNotA500(string body, string contentType, HttpStatusCode expected)
    {
        // This host runs as Development, where minimal APIs throw a binding failure rather than answer it,
        // as the demo laptop does: the exception handler keeps its 400 or 415, with the headers.
        using var client = api.CreateClient(new() { HandleCookies = false });
        using var content = new StringContent(body, Encoding.UTF8, contentType);

        using var response = await client.PostAsync(new Uri("/v1/admin/session", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        AssertHeaders(response);
    }

    [Fact]
    public async Task ARateLimitRefusalCarriesThemToo()
    {
        await using var limited = new RateLimitedApiFixture();
        await limited.InitializeAsync();
        using var client = limited.From("203.0.113.130");
        HttpResponseMessage? last = null;
        for (var i = 0; i <= RateLimitedApiFixture.Limit; i++)
        {
            last?.Dispose();
            last = await client.PostAsync(new Uri("/v1/admin/session/nonce", UriKind.Relative), content: null, TestContext.Current.CancellationToken);
        }

        using var refused = last!;
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        AssertHeaders(refused);
    }

    /// <summary>Fails any save that adds an owner, the way a dropped connection would.</summary>
    private sealed class FailOwnerInserts : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            return eventData.Context!.ChangeTracker.Entries<ScreenTail.Api.Data.Owner>().Any(e => e.State == EntityState.Added)
                ? throw new DbUpdateException("The database went away.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public void TheApiPolicyAllowsNoSourceAtAll()
    {
        Assert.StartsWith("default-src 'none';", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
    }
}
