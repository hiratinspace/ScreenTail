using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Hosting;

/// <summary>
/// What a deployment exposes (ST-007). The reverse proxy in <c>deploy/web/Caddyfile</c> forwards
/// <c>/v1/*</c> and <c>/health</c> to the API and serves the web page for everything else, so outside
/// Development the API must map nothing but those: a route mapped anywhere else would be unreachable
/// through the proxy (and if somebody widened the proxy to reach it, the OpenAPI map and the demo PSA
/// are exactly what must not be). The smoke test in <c>deploy/smoke.sh</c> checks the proxy's half.
/// </summary>
public sealed class DeploymentSurfaceTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public void InProductionEveryRouteIsHealthOrUnderV1()
    {
        using var production = api.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));

        var routes = Routes(production.Services);

        Assert.Contains("/health", routes);
        Assert.Contains(routes, r => r.StartsWith("/v1/", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, r => r != "/health" && !r.StartsWith("/v1/", StringComparison.Ordinal));
    }

    [Fact]
    public void InDevelopmentTheOpenApiMapIsTheRouteOutsideThem()
    {
        // The control: the enumeration does see a route outside /v1 when one is mapped.
        var routes = Routes(api.Services);

        Assert.Contains("/swagger/v1/swagger.json", routes);
    }

    [Fact]
    public void WithAProxyListedTheFrameworksOwnSwitchIsRefused()
    {
        var listed = new TrustedProxyOptions();
        listed.KnownProxies.Add("172.30.80.2");

        var thrown = Assert.Throws<InvalidOperationException>(() => listed.Validate("true"));
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", thrown.Message, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidOperationException>(() => listed.Validate(" True "));

        listed.Validate(null);
        listed.Validate("false");
        listed.Validate();

        // With nothing listed the switch changes nothing (ForwardedHeadersEnabledTests), so it is not refused.
        new TrustedProxyOptions().Validate("true");
    }

    [Fact]
    public void TheServiceDoesNotStartWithBoth()
    {
        using var both = api.WithWebHostBuilder(builder => builder
            .UseSetting("ForwardedHeaders:KnownProxies:0", "172.30.80.2")
            .UseSetting(TrustedProxyOptions.FrameworkSwitch, "true"));

        var thrown = Assert.ThrowsAny<Exception>(() => both.Services);
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", thrown.ToString(), StringComparison.Ordinal);
    }

    private static List<string> Routes(IServiceProvider services) =>
        [.. services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))];
}
