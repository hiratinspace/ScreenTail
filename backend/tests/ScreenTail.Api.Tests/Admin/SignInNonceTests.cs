using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// Replay protection for owner sign-in (ST-099 hardening, ADR-0009): a Google ID token is accepted only
/// with a nonce this service issued, that has not expired and has not been used, and the one that starts
/// a session is used up. Offline: the tokens are minted here and Google's keys are the fixture's.
/// </summary>
public sealed class SignInNonceTests(AdminApiFixture api) : IClassFixture<AdminApiFixture>
{
    private static readonly Uri NonceRoute = new("/v1/admin/session/nonce", UriKind.Relative);
    private static readonly Uri SignIn = new("/v1/admin/session", UriKind.Relative);
    private static readonly Uri Signup = new("/v1/admin/signup", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SubjectOfAsync(OwnerSession owner) =>
        await api.UseAsync(db => db.Owners.Where(o => o.Id == owner.OwnerId).Select(o => o.GoogleSubject).SingleAsync());

    private static async Task<IssuedNonce> AskForNonceAsync(HttpClient client)
    {
        using var response = await client.PostAsync(NonceRoute, content: null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<IssuedNonce>(Ct))!;
    }

    [Fact]
    public async Task TheHappyPathAsksForANonceAndSignsInWithATokenThatCarriesIt()
    {
        var owner = await api.SeedOwnerAsync("Nonce Happy");
        using var client = api.CreateClient(new() { HandleCookies = false });

        var issued = await AskForNonceAsync(client);
        using var response = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: await SubjectOfAsync(owner), nonce: issued.Nonce)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AdminSessionResponse.SignedIn, (await response.Content.ReadFromJsonAsync<AdminSessionResponse>(Ct))!.Status);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AdminSession.CookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANonceIsFreshEachTimeShortLivedAndOnlyItsHashIsKept()
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        var before = DateTimeOffset.UtcNow;
        var first = await AskForNonceAsync(client);
        var second = await AskForNonceAsync(client);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.Equal(SignInNonces.Length, first.Nonce.Length);
        Assert.InRange(first.ExpiresAt, before.AddMinutes(10).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5));
        Assert.True(await api.UseAsync(db => db.AdminSignInNonces.AnyAsync(n => n.Id == SignInNonces.Hash(first.Nonce))));
        Assert.False(await api.UseAsync(db => db.AdminSignInNonces.AnyAsync(n => n.Id == first.Nonce)));
    }

    [Fact]
    public async Task ATokenWithNoNonceIsRefusedOnBothRoutes()
    {
        var owner = await api.SeedOwnerAsync("Nonce Missing");
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var signIn = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: await SubjectOfAsync(owner), omitNonce: true)), Ct);
        using var signup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(api.Google.Mint(subject: "sub-nonce-missing", email: AdminApiFixture.Allowed[20], omitNonce: true), "No Nonce Ltd"), Ct);

        // Not even the first-run answer, which says the address may create a tenant.
        using var asked = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: "sub-nonce-missing", email: AdminApiFixture.Allowed[20], omitNonce: true)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signup.StatusCode);
        Assert.False(signIn.Headers.Contains("Set-Cookie"));
        Assert.False(signup.Headers.Contains("Set-Cookie"));
        Assert.False(await api.UseAsync(db => db.Owners.AnyAsync(o => o.GoogleSubject == "sub-nonce-missing")));
    }

    [Theory]
    [InlineData("never-issued-but-the-right-shape-0123456789")]
    [InlineData("short")]
    [InlineData("")]
    [InlineData("never issued, wrong shape, and far longer than anything this service hands out to a page")]
    public async Task ANonceThisServiceDidNotIssueIsRefused(string nonce)
    {
        var owner = await api.SeedOwnerAsync("Nonce Wrong");
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var signIn = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: await SubjectOfAsync(owner), nonce: nonce)), Ct);
        using var signup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(api.Google.Mint(subject: $"sub-nonce-wrong-{nonce.Length}", email: AdminApiFixture.Allowed[21], nonce: nonce), "Wrong Nonce Ltd"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signup.StatusCode);
        Assert.Contains("sign_in_failed", await signIn.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenThatStartedASessionCannotStartAnother()
    {
        var owner = await api.SeedOwnerAsync("Nonce Reuse");
        var token = api.Google.Mint(subject: await SubjectOfAsync(owner));
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var first = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), Ct);
        using var replay = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ATokenThatCreatedATenantCannotThenSignIn()
    {
        var token = api.Google.Mint(subject: "sub-nonce-signup-replay", email: AdminApiFixture.Allowed[22]);
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var asked = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), Ct);
        using var created = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Replay Co"), Ct);
        using var replay = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), Ct);
        using var replaySignup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Replay Co Again"), Ct);

        // Learning the account may create a tenant does not use the nonce up, so the first-run screen can
        // send the same token on; creating the tenant does.
        Assert.Equal(AdminSessionResponse.NeedsTenant, (await asked.Content.ReadFromJsonAsync<AdminSessionResponse>(Ct))!.Status);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replaySignup.StatusCode);
    }

    [Fact]
    public async Task ARefusedNameDoesNotUseTheNonceUp()
    {
        var token = api.Google.Mint(subject: "sub-nonce-rename", email: AdminApiFixture.Allowed[23]);
        using var client = api.CreateClient(new() { HandleCookies = false });

        using var refused = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "   "), Ct);
        using var created = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(token, "Renamed Co"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Fact]
    public async Task AnExpiredNonceIsRefused()
    {
        // The host's own clock, held: the nonce is issued by the route, then the clock passes its lifetime.
        // The Google token itself is good for thirty minutes, so only the nonce has run out.
        var clock = new HeldClock();
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(clock.Install));
        var owner = await api.SeedOwnerAsync("Nonce Expired");
        using var client = host.CreateClient(new() { HandleCookies = false });

        var issued = await AskForNonceAsync(client);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        using var expired = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: await SubjectOfAsync(owner), nonce: issued.Nonce)), Ct);

        var fresh = await AskForNonceAsync(client);
        clock.Advance(TimeSpan.FromMinutes(9));
        using var inTime = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: await SubjectOfAsync(owner), nonce: fresh.Nonce)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inTime.StatusCode);
    }

    [Fact]
    public async Task IssuingDeletesNoncesPastTheirExpiry()
    {
        var stale = api.IssueNonce(TimeSpan.FromSeconds(-1));
        Assert.True(await api.UseAsync(db => db.AdminSignInNonces.AnyAsync(n => n.Id == SignInNonces.Hash(stale.Nonce))));

        using var client = api.CreateClient(new() { HandleCookies = false });
        _ = await AskForNonceAsync(client);

        Assert.False(await api.UseAsync(db => db.AdminSignInNonces.AnyAsync(n => n.Id == SignInNonces.Hash(stale.Nonce))));
    }

    [Fact]
    public async Task OfTwoSignInsWithOneTokenAtOnceExactlyOneStartsASession()
    {
        var owner = await api.SeedOwnerAsync("Nonce Race");
        var token = api.Google.Mint(subject: await SubjectOfAsync(owner));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = api.CreateClient(new() { HandleCookies = false });
            using var response = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(token), Ct);
            return response.StatusCode;
        }));

        Assert.Equal(1, attempts.Count(s => s == HttpStatusCode.OK));
        Assert.All(attempts.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task WithSignInSwitchedOffNoNonceIsIssued()
    {
        using var host = api.WithWebHostBuilder(b => b.UseSetting("Admin:GoogleClientId", string.Empty));
        using var client = host.CreateClient(new() { HandleCookies = false });

        using var response = await client.PostAsync(NonceRoute, content: null, Ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public void TheLifetimeDefaultsToTenMinutesAndIsBounded()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), new AdminOptions().SignInNonceLifetime);
        var jwt = new JwtOptions { Audience = "screentail-api" };
        _ = Assert.Throws<InvalidOperationException>(() => new AdminOptions { SignInNonceLifetime = TimeSpan.FromSeconds(30) }.Validate(jwt));
        _ = Assert.Throws<InvalidOperationException>(() => new AdminOptions { SignInNonceLifetime = TimeSpan.FromHours(2) }.Validate(jwt));
        new AdminOptions { SignInNonceLifetime = TimeSpan.FromMinutes(5) }.Validate(jwt);
    }
}
