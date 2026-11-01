using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>The rate-limited API behind a listed proxy (10.0.0.1) and a listed proxy network (10.1.0.0/16).</summary>
public sealed class ProxiedApiFixture : RateLimitedApiFixture
{
    public const string Proxy = "10.0.0.1";

    public const string ProxyNetwork = "10.1.0.0/16";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.UseSetting("ForwardedHeaders:KnownProxies:0", Proxy);
        builder.UseSetting("ForwardedHeaders:KnownNetworks:0", ProxyNetwork);
    }
}

/// <summary>
/// Behind a reverse proxy the connection's address is the proxy's, so the rate limit reads the client
/// from X-Forwarded-For, but only on a connection from a proxy the operator listed, and only the entry
/// that proxy wrote (ST-099 hardening, ADR-0009).
/// </summary>
public sealed class TrustedProxyTests(ProxiedApiFixture api) : IClassFixture<ProxiedApiFixture>
{
    private static readonly Uri SignIn = new("/v1/admin/session", UriKind.Relative);

    private async Task<HttpStatusCode> SignInAsync(string peer, string? forwardedFor)
    {
        using var client = api.From(peer);
        if (forwardedFor is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        }

        using var response = await client.PostAsJsonAsync(SignIn, new AdminSignInRequest(api.Google.Mint(subject: $"sub-proxy-{Guid.NewGuid():N}", email: "stranger@elsewhere.example")), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private async Task SpendAsync(string peer, Func<int, string?> forwardedFor)
    {
        for (var i = 0; i < RateLimitedApiFixture.Limit; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, await SignInAsync(peer, forwardedFor(i)));
        }
    }

    [Fact]
    public async Task ClientsBehindAListedProxyAreCountedApart()
    {
        await SpendAsync(ProxiedApiFixture.Proxy, _ => "198.51.100.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(ProxiedApiFixture.Proxy, "198.51.100.1"));
        Assert.Equal(HttpStatusCode.Forbidden, await SignInAsync(ProxiedApiFixture.Proxy, "198.51.100.2"));
    }

    [Fact]
    public async Task AProxyInsideAListedNetworkIsTrustedToo()
    {
        await SpendAsync("10.1.2.3", _ => "198.51.100.3");

        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync("10.1.2.3", "198.51.100.3"));
        Assert.Equal(HttpStatusCode.Forbidden, await SignInAsync("10.1.2.3", "198.51.100.4"));
    }

    [Fact]
    public async Task ASpoofedHeaderFromAPeerThatIsNotAListedProxyDoesNotChangeThePartition()
    {
        await SpendAsync("203.0.113.90", i => $"198.51.100.{20 + i}");

        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync("203.0.113.90", "198.51.100.99"));
    }

    [Fact]
    public async Task LoopbackIsNotTrustedJustBecauseTheFrameworkWouldTrustItByDefault()
    {
        await SpendAsync("127.0.0.1", i => $"198.51.100.{40 + i}");

        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync("127.0.0.1", "198.51.100.98"));
    }

    [Fact]
    public async Task OnlyTheEntryTheProxyWroteCountsNotOnesTheClientPutInFront()
    {
        // The client sends "X-Forwarded-For: <anything>" and the proxy appends the address it saw. Only
        // the last entry is the proxy's word; the ones before it are the client's.
        await SpendAsync(ProxiedApiFixture.Proxy, i => $"192.0.2.{i}, 198.51.100.60");

        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(ProxiedApiFixture.Proxy, "192.0.2.200, 198.51.100.60"));
    }

    [Fact]
    public void NothingIsTrustedByDefaultAndABadEntryStopsStartup()
    {
        var none = new TrustedProxyOptions();
        Assert.False(none.Configured);
        none.Validate();

        var applied = new ForwardedHeadersOptions();
        var listed = new TrustedProxyOptions();
        listed.KnownProxies.Add("10.0.0.5");
        listed.KnownNetworks.Add("10.2.0.0/16");
        listed.Validate();
        listed.Apply(applied);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], applied.KnownProxies);
        Assert.Equal([System.Net.IPNetwork.Parse("10.2.0.0/16")], applied.KnownIPNetworks);
        Assert.Equal(1, applied.ForwardLimit);

        var badProxy = new TrustedProxyOptions();
        badProxy.KnownProxies.Add("proxy.example");
        _ = Assert.Throws<InvalidOperationException>(badProxy.Validate);

        var badNetwork = new TrustedProxyOptions();
        badNetwork.KnownNetworks.Add("10.0.0.0");
        _ = Assert.Throws<InvalidOperationException>(badNetwork.Validate);

        _ = Assert.Throws<InvalidOperationException>(new TrustedProxyOptions { ForwardLimit = 0 }.Validate);
    }
}
