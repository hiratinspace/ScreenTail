using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Logging;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Tests.Providers.ConnectWise;

namespace ScreenTail.Api.Tests.Logging;

/// <summary>
/// What the framework itself writes about a request (ST-011, INV-10). The hosting layer logs the request
/// line twice, before authentication runs, and the HTTP client logs every call to a tenant's PSA. A ticket
/// search carries the typed text in its query string, a route can carry a company's name, and a PSA call
/// names the tenant's site and a ticket. None of that may reach the log, whoever sends the request.
/// </summary>
public sealed class RequestLoggingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    // Words that appear only in what the requests carry, never in a route template or a seeded row.
    private static readonly string[] Content = ["Acme", "Dental", "printer", "Zürich", "brightsmiles", "PRIV-7731"];

    private const string Site = "https://psa.brightsmiles-it.example";

    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public async Task NoRequestLineCarriesTheQueryOrARouteValue(LogLevel level)
    {
        using var sink = new StringWriter();
        using var host = HostWith(sink, level, new ScriptedConnectWise());
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = host.CreateClient();
        using var anonymous = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        var ct = TestContext.Current.CancellationToken;

        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest(Site, "tenant+PUB:PRIV-7731"), ct);
        using var search = await client.GetAsync(new Uri("/v1/psa/tickets?q=Acme%20Dental%20printer", UriKind.Relative), ct);
        using var unicode = await client.GetAsync(new Uri("/v1/psa/tickets?q=Z%C3%BCrich+Dental&q=printer", UriKind.Relative), ct);
        using var refused = await anonymous.GetAsync(new Uri("/v1/psa/tickets?q=Acme%20Dental%20printer", UriKind.Relative), ct);
        using var unmap = await client.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Acme%20Dental", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unmap.StatusCode);
        var log = sink.ToString();

        // Not vacuous: the request lines and the PSA call were logged, just without what they carried.
        Assert.Contains("Request starting", log, StringComparison.Ordinal);
        Assert.Contains("Request finished", log, StringComparison.Ordinal);
        Assert.Contains("HTTP request", log, StringComparison.Ordinal);
        Assert.Contains("GET http://localhost", log, StringComparison.Ordinal);
        Assert.Contains(" - 401 ", log, StringComparison.Ordinal);
        foreach (var word in Content)
        {
            Assert.DoesNotContain(word, log, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Z%C3%BCrich", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACallToAPsaIsLoggedWithoutItsSiteTicketOrConditions()
    {
        using var sink = new StringWriter();
        var services = new ServiceCollection();
        _ = services.AddLogging(logging => logging.ClearProviders().AddProvider(new ScrubbingLoggerProvider(sink, LogLevel.Trace)).SetMinimumLevel(LogLevel.Trace));
        _ = services.AddHttpClient("psa").ConfigurePrimaryHttpMessageHandler(() => new Answers());
        await using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("psa");

        using var response = await http.GetAsync(new Uri(Site + "/v4_6_release/apis/3.0/service/tickets/48213/notes?conditions=company/name%20like%20%22Acme%25%22"), TestContext.Current.CancellationToken);

        var log = sink.ToString();
        Assert.Contains("HTTP request", log, StringComparison.Ordinal);
        Assert.DoesNotContain("brightsmiles", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("48213", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScrubbingLoggerIsTheOnlySink()
    {
        // The service's own composition, not a test's: ClearProviders and then this one provider. A
        // second provider (a JSON console, the debug sink, an exporter) would receive every framework line
        // before any scrubbing, so the test is that there is exactly one.
        var provider = Assert.Single(api.Services.GetServices<ILoggerProvider>());

        Assert.IsType<ScrubbingLoggerProvider>(provider);
    }

    private WebApplicationFactory<Program> HostWith(StringWriter sink, LogLevel level, ScriptedConnectWise cw) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.UseSetting("Logging:LogLevel:Default", level.ToString());
        _ = builder.ConfigureTestServices(services =>
        {
            // The same provider the service installs, over a writer the test can read.
            _ = services.RemoveAll<ILoggerProvider>();
            _ = services.AddSingleton<ILoggerProvider>(new ScrubbingLoggerProvider(sink, level));
            _ = services.Configure<HttpClientFactoryOptions>(
                nameof(ConnectWiseProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw));
        });
    });

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "A tenant", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@tenant.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }

    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
    }
}
