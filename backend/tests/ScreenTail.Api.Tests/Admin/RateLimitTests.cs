using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// The API with the anonymous routes' real limit: three calls an hour from one address, and the test
/// host's connection given the address a test names (the test server has none of its own).
/// </summary>
public class RateLimitedApiFixture : AdminApiFixture
{
    public const int Limit = 3;

    /// <summary>Test plumbing only: the header this host turns into the connection's address. The service reads no such header.</summary>
    public const string ClientAddressHeader = "X-Test-Connection-Address";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:PermitLimit", Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RateLimit:Window", "01:00:00");
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ConnectionAddressFromTestHeader>());
    }

    /// <summary>A client whose every request arrives from this address.</summary>
    public HttpClient From(string address)
    {
        var client = CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add(ClientAddressHeader, address);
        return client;
    }

    private sealed class ConnectionAddressFromTestHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            _ = app.Use((context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[ClientAddressHeader].ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>
/// The anonymous routes are counted per client address and the call past the limit is refused with 429,
/// a Retry-After and nothing else, before anything checks it (ST-099 hardening, ADR-0009).
/// </summary>
public sealed class RateLimitTests(RateLimitedApiFixture api) : IClassFixture<RateLimitedApiFixture>
{
    private static readonly Uri SignIn = new("/v1/admin/session", UriKind.Relative);
    private static readonly Uri Signup = new("/v1/admin/signup", UriKind.Relative);
    private static readonly Uri SignOut = new("/v1/admin/session/end", UriKind.Relative);
    private static readonly Uri NonceRoute = new("/v1/admin/session/nonce", UriKind.Relative);
    private static readonly Uri Activate = new("/v1/devices/activate", UriKind.Relative);
    private static readonly Uri Exchange = new("/v1/devices/token", UriKind.Relative);

    private async Task<HttpResponseMessage> SignInAsync(HttpClient client) =>
        await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: $"sub-limit-{Guid.NewGuid():N}", email: "stranger@elsewhere.example")), TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheCallPastTheLimitIsRefusedWith429ARetryAfterAndNoBody()
    {
        using var client = api.From("203.0.113.10");
        for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
        {
            using var allowed = await SignInAsync(client);
            Assert.Equal(HttpStatusCode.Forbidden, allowed.StatusCode);
        }

        using var refused = await SignInAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var retryAfter = refused.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        Assert.Empty(await refused.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.True(refused.Headers.CacheControl?.NoStore);
        Assert.False(refused.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task AnotherAddressIsUnaffected()
    {
        using (var busy = api.From("203.0.113.20"))
        {
            for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
            {
                using var _ = await SignInAsync(busy);
            }

            using var refused = await SignInAsync(busy);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        }

        using var other = api.From("203.0.113.21");
        using var answered = await SignInAsync(other);
        Assert.Equal(HttpStatusCode.Forbidden, answered.StatusCode);
    }

    [Fact]
    public async Task ARefusedCallNeverReachesTheTokenVerifier()
    {
        using var client = api.From("203.0.113.30");
        for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
        {
            using var _ = await SignInAsync(client);
        }

        var checkedBefore = api.Keys.Calls;
        using var refused = await SignInAsync(client);
        using var refusedSignup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest(api.Google.Mint(subject: "sub-limit-signup", email: AdminApiFixture.Allowed[39]), "Never Made"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refusedSignup.StatusCode);
        Assert.Equal(checkedBefore, api.Keys.Calls);
    }

    [Fact]
    public async Task AForwardedHeaderDoesNotChooseThePartition()
    {
        using var client = api.From("203.0.113.40");
        for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
        {
            using var _ = await SignInAsync(client);
        }

        // A caller who writes its own X-Forwarded-For (or Forwarded, or X-Real-IP) is still counted by the
        // address it connected from: no proxy is trusted.
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.77");
        client.DefaultRequestHeaders.Add("Forwarded", "for=198.51.100.78");
        client.DefaultRequestHeaders.Add("X-Real-IP", "198.51.100.79");
        using var refused = await SignInAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task TheNonceSignInAndSignupShareOneCount()
    {
        using var client = api.From("203.0.113.50");
        using var nonce = await client.PostAsync(NonceRoute, content: null, TestContext.Current.CancellationToken);
        using var signIn = await SignInAsync(client);
        using var signup = await client.PostAsJsonAsync(Signup, new AdminSignupRequest("not-a-token", "Nobody"), TestContext.Current.CancellationToken);
        using var refused = await client.PostAsync(NonceRoute, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, nonce.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signup.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task SignOutStillEndsTheSessionOnceTheLimitIsSpent()
    {
        // Revoking a stolen cookie must never wait for a window to pass, so sign-out is not counted.
        var owner = await api.SeedOwnerAsync("Limit Sign-out");
        const string address = "203.0.113.55";
        using (var anonymous = api.From(address))
        {
            for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
            {
                using var _ = await SignInAsync(anonymous);
            }

            using var spent = await SignInAsync(anonymous);
            Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);
        }

        using var client = api.Owner(owner);
        client.DefaultRequestHeaders.Add(RateLimitedApiFixture.ClientAddressHeader, address);
        using var signOut = await client.PostAsync(SignOut, content: null, TestContext.Current.CancellationToken);
        using var after = await client.GetAsync(SignIn, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task DeviceActivationIsLimitedTooSoInviteCodesCannotBeGuessedAtSpeed()
    {
        using var client = api.From("203.0.113.60");
        for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
        {
            using var wrong = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest($"WRONG{i}CODE", "TECH-PC"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var refused = await client.PostAsJsonAsync(Activate, new ActivateDeviceRequest("WRONG9CODE", "TECH-PC"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task TheTokenExchangeAndOwnerRoutesAreNotCounted()
    {
        // A device renews its access token every fifty minutes behind the MSP's one address, with a
        // 256-bit secret nobody guesses; an owner's routes need a session. Neither is the anonymous limit's.
        using var client = api.From("203.0.113.70");
        for (var i = 0; i < RateLimitedApiFixture.Limit + 2; i++)
        {
            using var exchange = await client.PostAsJsonAsync(Exchange, new RefreshRequest("not-a-refresh-token"), TestContext.Current.CancellationToken);
            using var session = await client.GetAsync(SignIn, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, exchange.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void AnAddressIsCountedAsItsSubscriber(string address, string partition) =>
        Assert.Equal(partition, AnonymousRateLimit.PartitionKey(IPAddress.Parse(address)));

    [Fact]
    public void TwoAddressesInOneIpv6SlashSixtyFourShareACountAndTheNextOneDoesNot()
    {
        Assert.Equal(AnonymousRateLimit.PartitionKey(IPAddress.Parse("2001:db8:0:1::1")), AnonymousRateLimit.PartitionKey(IPAddress.Parse("2001:db8:0:1:ffff::9")));
        Assert.NotEqual(AnonymousRateLimit.PartitionKey(IPAddress.Parse("2001:db8:0:1::1")), AnonymousRateLimit.PartitionKey(IPAddress.Parse("2001:db8:0:2::1")));
        Assert.Equal("none", AnonymousRateLimit.PartitionKey(null));
    }

    [Fact]
    public void TheDefaultsAreTenAMinuteAndTheBoundsAreChecked()
    {
        var defaults = new RateLimitOptions();
        Assert.Equal(10, defaults.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), defaults.Window);
        defaults.Validate();

        var bound = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimit:PermitLimit"] = "25", ["RateLimit:Window"] = "00:05:00" })
            .Build().GetSection(RateLimitOptions.Section).Get<RateLimitOptions>()!;
        Assert.Equal(25, bound.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), bound.Window);

        _ = Assert.Throws<InvalidOperationException>(() => new RateLimitOptions { PermitLimit = 0 }.Validate());
        _ = Assert.Throws<InvalidOperationException>(() => new RateLimitOptions { Window = TimeSpan.Zero }.Validate());
        _ = Assert.Throws<InvalidOperationException>(() => new RateLimitOptions { Window = TimeSpan.FromHours(2) }.Validate());
    }
}
