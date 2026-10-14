using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

        // A slash in the query. At the root the path is "/" too, which once hid the query from redaction.
        using var slashed = await client.GetAsync(new Uri("/v1/psa/tickets?q=Acme/Dental", UriKind.Relative), ct);
        using var root = await anonymous.GetAsync(new Uri("/?q=Acme/Dental", UriKind.Relative), ct);
        using var unmap = await client.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Acme%20Dental", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, slashed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unmap.StatusCode);
        var log = Read(sink);

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

    [Theory]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public async Task OutsideDevelopmentARedirectToHttpsIsLoggedWithoutTheQuery(LogLevel level)
    {
        // The pipeline that ships: HSTS and the HTTPS redirect run only outside Development, and the
        // redirect logs where it sends the request, which is the whole URL with its query.
        using var sink = new StringWriter();
        using var host = HostWith(sink, level, new ScriptedConnectWise(), Environments.Production);
        using var plain = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await plain.GetAsync(new Uri("/v1/psa/tickets?q=Acme%20Dental%20printer", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://localhost/v1/psa/tickets?q=Acme%20Dental%20printer", response.Headers.Location?.AbsoluteUri);
        var log = Read(sink);
        Assert.Contains("Redirecting to", log, StringComparison.Ordinal);
        Assert.Contains("Request finished", log, StringComparison.Ordinal);
        foreach (var word in Content)
        {
            Assert.DoesNotContain(word, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Trace)]
    public async Task OverKestrelARequestItCannotParseIsLoggedWithoutItsLineOrHeaders(LogLevel level)
    {
        // The server that ships, not the in-memory one: Kestrel rejects a request it cannot parse before
        // any of the app runs, and its exception quotes the raw line it choked on. A request line with a
        // space in the query, and a header line with a space before its colon, carry a search and a token.
        using var sink = new StringWriter();
        using var host = HostWith(sink, level, new ScriptedConnectWise());
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        host.StartServer();
        var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var ct = TestContext.Current.CancellationToken;

        var line = await SendRawAsync(address, "GET /v1/psa/tickets?q=Acme Dental printer HTTP/1.1\r\nHost: localhost\r\n\r\n", ct);
        var header = await SendRawAsync(address, "GET /v1/psa/tickets HTTP/1.1\r\nHost: localhost\r\nAuthorization : Bearer Acme-token-PRIV-7731\r\n\r\n", ct);

        Assert.StartsWith("HTTP/1.1 400", line, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", header, StringComparison.Ordinal);
        var log = await LoggedAsync(sink, "bad request data", times: 2, ct);
        Assert.Contains("BadHttpRequestException: 400 [redacted]", log, StringComparison.Ordinal);
        foreach (var word in Content)
        {
            Assert.DoesNotContain(word, log, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Bearer", log, StringComparison.OrdinalIgnoreCase);
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

        var log = Read(sink);
        Assert.Contains("HTTP request", log, StringComparison.Ordinal);
        Assert.DoesNotContain("brightsmiles", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("48213", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", log, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void TheScrubbingLoggerIsTheOnlySink(string environment)
    {
        // The service's own composition, not a test's: ClearProviders and then this one provider. A
        // second provider (a JSON console, the debug sink, an exporter) would receive every framework line
        // before any scrubbing, so the test is that there is exactly one. Production as well, because a
        // sink added only outside Development would never show up in a Development host.
        using var host = api.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        var provider = Assert.Single(host.Services.GetServices<ILoggerProvider>());

        Assert.IsType<ScrubbingLoggerProvider>(provider);
    }

    private WebApplicationFactory<Program> HostWith(StringWriter sink, LogLevel level, ScriptedConnectWise cw, string? environment = null) => api.WithWebHostBuilder(builder =>
    {
        if (environment is not null)
        {
            // Outside Development the HTTPS redirect needs to know the port it sends a request to.
            _ = builder.UseEnvironment(environment);
            _ = builder.UseSetting("https_port", "443");
        }

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

    // The host keeps logging on its own threads after a response (connection and scope lines at Trace).
    // The provider writes under a lock on the writer, so the test reads under the same lock.
    private static string Read(StringWriter sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }

    // One request written as raw bytes, which no HTTP client would send, and the answer read to the end:
    // Kestrel closes the connection after a request it rejects.
    private static async Task<string> SendRawAsync(Uri address, string request, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, ct);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(ct);
    }

    // Kestrel writes its line on the connection's own thread, so the test waits for it, for a bounded time.
    private static async Task<string> LoggedAsync(StringWriter sink, string expected, int times, CancellationToken ct)
    {
        var log = Read(sink);
        for (var wait = 0; wait < 100 && log.Split(expected).Length - 1 < times; wait++)
        {
            await Task.Delay(50, ct);
            log = Read(sink);
        }

        Assert.Equal(times, log.Split(expected).Length - 1);
        return log;
    }

    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
    }
}
