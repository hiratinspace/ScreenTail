using System.Net;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// The rule that keeps PSA and documentation calls on the public internet (security check 2026-10-14):
/// which addresses count as public, and a handler that checks at connect time and connects to exactly
/// what it checked. The listener stands in for a service on the backend's own network.
/// </summary>
public sealed class PublicNetworkTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.9")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.170")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("3fff::1")]
    public void AnAddressOffThePublicInternetIsNotPublic(string address) =>
        Assert.False(PublicNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("192.169.0.0")]
    [InlineData("223.255.255.255")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    public void AnAddressOnThePublicInternetIsPublic(string address) =>
        Assert.True(PublicNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("2130706433")]
    public async Task AnAddressLiteralOnThisMachineIsRefusedBeforeAnythingConnects(string host)
    {
        using var listener = new LoopbackListener(Answer204);
        var lookups = 0;
        using var client = new HttpClient(PublicNetwork.Handler(allowPrivate: false, (_, _) =>
        {
            _ = Interlocked.Increment(ref lookups);
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        }));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://{host}:{listener.Port}/"), TestContext.Current.CancellationToken));

        Assert.True(PublicNetwork.WasRefused(failure));
        Assert.Equal(0, lookups);
        Assert.Equal(0, listener.Accepted);
    }

    [Fact]
    public async Task ANameThatResolvesToAPrivateAddressIsRefused()
    {
        using var listener = new LoopbackListener(Answer204);
        using var client = new HttpClient(PublicNetwork.Handler(allowPrivate: false, Resolve(IPAddress.Loopback)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://psa.example:{listener.Port}/"), TestContext.Current.CancellationToken));

        Assert.True(PublicNetwork.WasRefused(failure));
        Assert.Equal(0, listener.Accepted);
    }

    [Fact]
    public async Task ANameThatResolvesToAPublicAndAPrivateAddressIsRefusedOutright()
    {
        // The public address comes first; a handler that tried addresses in order would never reach the
        // private one here, and would elsewhere. Refusing the whole answer does not depend on the order.
        using var listener = new LoopbackListener(Answer204);
        using var client = new HttpClient(PublicNetwork.Handler(allowPrivate: false, Resolve(IPAddress.Parse("8.8.8.8"), IPAddress.Loopback)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://psa.example:{listener.Port}/"), TestContext.Current.CancellationToken));

        Assert.True(PublicNetwork.WasRefused(failure));
        Assert.Equal(0, listener.Accepted);
    }

    [Fact]
    public async Task TheConnectionGoesToTheAddressThatWasCheckedWithNoSecondLookup()
    {
        // DNS rebinding answers a check with one address and the connection with another. Here the name
        // does not exist outside this test's resolver, so reaching the listener at all proves the socket
        // went to the checked address rather than to a lookup of its own, and the count proves there was
        // one lookup for the one connection.
        using var listener = new LoopbackListener(Answer204);
        var lookups = 0;
        using var client = new HttpClient(PublicNetwork.Handler(allowPrivate: true, (_, _) =>
        {
            _ = Interlocked.Increment(ref lookups);
            return Task.FromResult(new[] { IPAddress.Loopback });
        }));

        using var response = await client.GetAsync(new Uri($"http://rebind.invalid:{listener.Port}/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, lookups);
        Assert.Equal(1, listener.Accepted);
    }

    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        // A redirect would carry the provider's key header to whatever host it names.
        using var elsewhere = new LoopbackListener(Answer204);
        using var site = new LoopbackListener($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{elsewhere.Port}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new HttpClient(PublicNetwork.Handler(allowPrivate: true));

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{site.Port}/"), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(0, elsewhere.Accepted);
    }

    [Fact]
    public void NoProxyStandsBetweenTheCheckAndTheConnection()
    {
        // A proxy resolves the name itself, so the check would be of the proxy and not of the site.
        using var handler = PublicNetwork.Handler(allowPrivate: false);

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Theory]
    [InlineData("https://localhost", true)]
    [InlineData("https://Localhost.", true)]
    [InlineData("https://a.b.localhost", true)]
    [InlineData("https://127.0.0.1", true)]
    [InlineData("https://[::1]", true)]
    [InlineData("https://[fe80::1%25eth0]", true)]
    [InlineData("https://192.168.1.1:8443", true)]
    [InlineData("https://na.myconnectwise.net", false)]
    [InlineData("https://localhost.example.com", false)]
    [InlineData("https://8.8.8.8", false)]
    public void ASiteIsPlainlyPrivateOnlyWhenItSaysSoWithoutALookup(string site, bool plainlyPrivate) =>
        Assert.Equal(plainlyPrivate, PublicNetwork.IsPlainlyPrivate(new Uri(site)));

    [Fact]
    public void TheOptionIsRefusedOutsideDevelopmentAndAllowedInIt()
    {
        var on = new ProviderNetworkOptions { AllowPrivateAddresses = true };

        _ = Assert.Throws<InvalidOperationException>(() => on.Validate(isDevelopment: false));
        on.Validate(isDevelopment: true);
        new ProviderNetworkOptions().Validate(isDevelopment: false);
    }

    private const string Answer204 = "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n";

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolve(params IPAddress[] addresses) =>
        (_, _) => Task.FromResult(addresses);
}
