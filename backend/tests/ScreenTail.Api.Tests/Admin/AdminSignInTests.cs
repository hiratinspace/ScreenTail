using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// The owner's way in (ST-099 AC "owner can sign up", ADR-0009): a Google account on the signup list
/// creates a tenant on its first sign-in and becomes its owner; any other account is refused; an owner
/// who exists signs in whatever the list says; and the session is a cookie the page's script cannot read.
/// </summary>
public sealed class AdminSignInTests(AdminApiFixture api) : IClassFixture<AdminApiFixture>
{
    private static readonly Uri SignIn = new("/v1/admin/session", UriKind.Relative);
    private static readonly Uri Signup = new("/v1/admin/signup", UriKind.Relative);

    [Fact]
    public async Task AnAllowedAccountSignsUpCreatesItsTenantAndBecomesItsOwner()
    {
        var email = AdminApiFixture.Allowed[0];
        var token = api.Google.Mint(subject: "sub-signup-0", email: email);
        using var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using var first = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), TestContext.Current.CancellationToken);
        var asked = await first.Content.ReadFromJsonAsync<AdminSessionResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(AdminSessionResponse.NeedsTenant, asked!.Status);
        Assert.Equal(email, asked.Email);
        Assert.Null(asked.Csrf);
        Assert.False(first.Headers.Contains("Set-Cookie"));

        using var created = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "  Acme IT  "), TestContext.Current.CancellationToken);
        var session = await created.Content.ReadFromJsonAsync<AdminSessionResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(AdminSessionResponse.SignedIn, session!.Status);
        Assert.Equal("Acme IT", session.Tenant!.Name);
        Assert.Equal(AdminApiFixture.NewTenantSeats, session.Tenant.Seats);
        Assert.Equal(email, session.Owner!.Email);
        Assert.False(string.IsNullOrEmpty(session.Csrf));

        var owner = await api.UseAsync(db => db.Owners.Include(o => o.Tenant).SingleAsync(o => o.GoogleSubject == "sub-signup-0"));
        Assert.Equal(session.Tenant.Id, owner.TenantId);
        Assert.Equal(AdminApiFixture.NewTenantSeats, owner.Tenant!.Seats);

        // The cookie the browser keeps carries the session: the same client is now signed in.
        client.DefaultRequestHeaders.Add(AdminSession.CsrfHeader, session.Csrf);
        var again = await client.GetFromJsonAsync<AdminSessionResponse>(SignIn, TestContext.Current.CancellationToken);
        Assert.Equal(session.Tenant.Id, again!.Tenant!.Id);
        Assert.Equal(session.Csrf, again.Csrf);
    }

    [Fact]
    public async Task TheSessionCookieIsHostOnlyHttpOnlySecureAndStrict()
    {
        var token = api.Google.Mint(subject: "sub-cookie", email: AdminApiFixture.Allowed[1]);
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var created = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Birch Legal"), TestContext.Current.CancellationToken);
        var cookie = Assert.Single(created.Headers.GetValues("Set-Cookie"));

        Assert.StartsWith(AdminSession.CookieName + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);

        // And the session itself is not in the body, where the page's script could read it.
        var body = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var value = cookie[(AdminSession.CookieName.Length + 1)..cookie.IndexOf(';', StringComparison.Ordinal)];
        Assert.DoesNotContain(value, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNotOnTheListCannotSignUpAndNoTenantIsMade()
    {
        var before = await api.UseAsync(db => db.Tenants.CountAsync());
        var token = api.Google.Mint(subject: "sub-stranger", email: "stranger@elsewhere.example");
        using var client = api.CreateClient();

        using var signIn = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), TestContext.Current.CancellationToken);
        using var signup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Stranger Co"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, signup.StatusCode);
        Assert.Contains("signup_not_allowed", await signup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(signup.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, await api.UseAsync(db => db.Tenants.CountAsync()));
        Assert.False(await api.UseAsync(db => db.Owners.AnyAsync(o => o.GoogleSubject == "sub-stranger")));
    }

    [Fact]
    public async Task TheListIsComparedWithoutCaseButNotByPrefixOrSuffix()
    {
        using var client = api.CreateClient();
        using var upper = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: "sub-case", email: AdminApiFixture.Allowed[2].ToUpperInvariant())), TestContext.Current.CancellationToken);
        using var longer = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: "sub-longer", email: "x" + AdminApiFixture.Allowed[2])), TestContext.Current.CancellationToken);
        using var shorter = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: "sub-shorter", email: AdminApiFixture.Allowed[2] + ".evil")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, longer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, shorter.StatusCode);
    }

    [Fact]
    public async Task AnExistingOwnerSignsInEvenWhenTheirAddressIsNoLongerOnTheList()
    {
        // Seeded with an address that was never on the list: the list decides who creates a tenant, not
        // who runs one.
        var seeded = await api.SeedOwnerAsync("Cedar MSP");
        var subject = await api.UseAsync(db => db.Owners.Where(o => o.Id == seeded.OwnerId).Select(o => o.GoogleSubject).SingleAsync());
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var response = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: subject, email: "renamed@cedar.example")), TestContext.Current.CancellationToken);
        var session = await response.Content.ReadFromJsonAsync<AdminSessionResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AdminSessionResponse.SignedIn, session!.Status);
        Assert.Equal(seeded.TenantId, session.Tenant!.Id);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSession.CookieName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneGoogleAccountRunsOneTenant()
    {
        var token = api.Google.Mint(subject: "sub-twice", email: AdminApiFixture.Allowed[3]);
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var first = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "First"), TestContext.Current.CancellationToken);
        using var second = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Second"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await api.UseAsync(db => db.Owners.CountAsync(o => o.GoogleSubject == "sub-twice")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ATenantNeedsAName(string name)
    {
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(api.Google.Mint(subject: $"sub-noname-{name.Length}", email: AdminApiFixture.Allowed[4]), name), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ATokenGoogleDidNotIssueForThisDeploymentIsRefusedOnBothRoutes()
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        foreach (var token in new[]
        {
            api.Google.Mint(subject: "sub-bad", email: AdminApiFixture.Allowed[5], audience: "999-somebody-else.apps.googleusercontent.com"),
            api.Google.Mint(subject: "sub-bad", email: AdminApiFixture.Allowed[5], issuer: "https://evil.example"),
            api.Google.Mint(subject: "sub-bad", email: AdminApiFixture.Allowed[5], expires: DateTimeOffset.UtcNow.AddMinutes(-1)),
            api.Google.Mint(subject: "sub-bad", email: AdminApiFixture.Allowed[5], emailVerified: false),
            api.Google.Mint(subject: "sub-bad", email: AdminApiFixture.Allowed[5], signWith: api.Google.Other(GoogleTokens.KeyId)),
        })
        {
            using var signIn = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), TestContext.Current.CancellationToken);
            using var signup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Bad"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, signup.StatusCode);
            Assert.False(signup.Headers.Contains("Set-Cookie"));
        }

        Assert.False(await api.UseAsync(db => db.Owners.AnyAsync(o => o.GoogleSubject == "sub-bad")));
    }

    [Fact]
    public async Task WhenGooglesKeysCannotBeHadSignInSaysSoAndLetsNobodyIn()
    {
        api.Keys.Unavailable = true;
        try
        {
            using var client = api.CreateClient();
            using var response = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(email: AdminApiFixture.Allowed[6])), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("could not reach Google", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            api.Keys.Unavailable = false;
        }
    }

    [Fact]
    public async Task ADisabledOwnerOrTenantCannotSignIn()
    {
        var seeded = await api.SeedOwnerAsync();
        var subject = await api.UseAsync(db => db.Owners.Where(o => o.Id == seeded.OwnerId).Select(o => o.GoogleSubject).SingleAsync());
        _ = await api.UseAsync(db => db.Tenants.Where(t => t.Id == seeded.TenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DisabledAt, DateTimeOffset.UtcNow)));
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var response = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: subject)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task SigningOutDeletesTheCookie()
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        using var response = await client.PostAsync(new Uri("/v1/admin/session/end", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.StartsWith(AdminSession.CookieName + "=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>With no Google client id the feature is off: both sign-in routes say so and nothing else changes.</summary>
public sealed class AdminSignInNotConfiguredTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData("/v1/admin/session")]
    [InlineData("/v1/admin/signup")]
    public async Task SignInIsRefusedAndNamesTheSetting(string path)
    {
        using var google = new GoogleTokens();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), new AdminSignupRequest(google.Mint(), "Acme"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("Admin:GoogleClientId", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
