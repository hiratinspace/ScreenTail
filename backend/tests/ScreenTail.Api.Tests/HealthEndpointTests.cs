using System.Net;
using System.Net.Http.Json;

namespace ScreenTail.Api.Tests;

/// <summary>
/// Liveness. It checks no dependencies and returns no data, so a load balancer asking every second
/// learns nothing about a tenant and costs nothing to answer (INV-7, INV-10).
/// </summary>
public sealed class HealthEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task HealthReturnsOkWithNoData()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new HealthBody("ok"), body);
    }

    [Fact]
    public async Task HealthDoesNotNeedAToken()
    {
        // Deliberate, and the only endpoint it is true of: an orchestrator restarting the service because
        // its probe got a 401 would be an outage caused by the check for one.
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record HealthBody(string Status);
}
