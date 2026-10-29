using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Providers.ConnectWise;

/// <summary>
/// The provider a tenant gets is built from the credential the vault holds for it, and nothing else
/// (ST-091 with ST-009). No credential, no provider; the client sees "Connect a PSA to publish".
/// </summary>
public sealed class ConnectWiseFactoryTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    [Fact]
    public async Task ATenantWithACredentialGetsAProviderPointedAtItsSite()
    {
        await using var db = await OpenAsync();
        var tenant = Guid.NewGuid();
        var vault = new IntegrationVault(db, Options(), TimeProvider.System);
        await vault.StoreAsync(tenant, "connectwise", "https://eu.myconnectwise.net", "acme+PUB:PRIV", TestContext.Current.CancellationToken);
        var factory = new ConnectWiseProviderFactory(vault, new OneClientFactory(), new ConnectWiseOptions { ClientId = "id" });

        var provider = await factory.ForTenantAsync(tenant, TestContext.Current.CancellationToken);

        Assert.NotNull(provider);
        Assert.Equal("connectwise", provider.Name);
    }

    [Fact]
    public async Task TwoTenantsGetTheirOwnSites()
    {
        // T-213: the scripted API matches routes by suffix, so a provider pointed at the wrong site, or
        // with the API path dropped, would still have passed every other test. Each tenant's calls go
        // to that tenant's site under the API path, with that tenant's key, and a trailing slash on the
        // stored site changes nothing.
        await using var db = await OpenAsync();
        var (na, eu) = (Guid.NewGuid(), Guid.NewGuid());
        var vault = new IntegrationVault(db, Options(), TimeProvider.System);
        await vault.StoreAsync(na, "connectwise", "https://na.myconnectwise.net", "north+PUB:PRIV", TestContext.Current.CancellationToken);
        await vault.StoreAsync(eu, "connectwise", "https://eu.myconnectwise.net/", "europe+PUB:PRIV", TestContext.Current.CancellationToken);
        using var api = new ScriptedConnectWise();
        var factory = new ConnectWiseProviderFactory(vault, new SharedHandlerFactory(api), new ConnectWiseOptions { ClientId = "id", BackoffBase = TimeSpan.Zero });

        var north = await factory.ForTenantAsync(na, TestContext.Current.CancellationToken);
        var europe = await factory.ForTenantAsync(eu, TestContext.Current.CancellationToken);
        Assert.True((await north!.CheckAsync(TestContext.Current.CancellationToken)).Ok);
        Assert.True((await europe!.CheckAsync(TestContext.Current.CancellationToken)).Ok);

        var asked = api.Requests;
        Assert.Equal(new Uri("https://na.myconnectwise.net/v4_6_release/apis/3.0/system/info"), asked[0].RequestUri);
        Assert.Equal(new Uri("https://eu.myconnectwise.net/v4_6_release/apis/3.0/system/info"), asked[1].RequestUri);
        Assert.Equal(Basic("north+PUB:PRIV"), asked[0].Headers.Authorization!.Parameter);
        Assert.Equal(Basic("europe+PUB:PRIV"), asked[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ATenantWithNoCredentialGetsNothing()
    {
        await using var db = await OpenAsync();
        var factory = new ConnectWiseProviderFactory(new IntegrationVault(db, Options(), TimeProvider.System), new OneClientFactory(), new ConnectWiseOptions());

        Assert.Null(await factory.ForTenantAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    private static VaultOptions Options() => new() { MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

    private async Task<ScreenTailContext> OpenAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        var db = new ScreenTailContext(new DbContextOptionsBuilder<ScreenTailContext>().UseSqlite(_connection).Options);
        _ = await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class OneClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ScriptedConnectWise());
    }

    /// <summary>A fresh client per provider, as the real factory gives, all over one recording API.</summary>
    private sealed class SharedHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string Basic(string credential) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential));
}

/// <summary>
/// The client's way to search (ST-092): <c>GET /v1/psa/tickets?q=</c>, answered from the tenant's own
/// PSA through the vault, or refused with a reason when there is none.
/// </summary>
public sealed class TicketSearchEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task ASearchGoesToTheTenantsConnectWiseAndComesBackAsRows()
    {
        var cw = new ScriptedConnectWise();
        using var host = HostWith(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);

        using var response = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var found = await response.Content.ReadFromJsonAsync<TicketSearchResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["48213", "48190"], found!.Tickets.Select(t => t.Id));
        Assert.Equal("Acme Dental", found.Tickets[0].Company);
        Assert.Equal("Basic", Assert.Single(cw.Requests).Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task WithNoPsaConnectedTheSearchSaysSo()
    {
        using var host = HostWith(new ScriptedConnectWise());
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("no_psa", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderRefusalIsPassedOnWithItsKindAndWords()
    {
        var cw = new ScriptedConnectWise { Failing = HttpStatusCode.Unauthorized };
        using var host = HostWith(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:WRONG"), TestContext.Current.CancellationToken);

        using var response = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("unauthenticated", body, StringComparison.Ordinal);
        Assert.Contains("Settings", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitedSearchPassesOnTheWaitRoundedAndHeldToADay()
    {
        // The same whole seconds, at most a day, that a publish result and the summarize route give: a
        // client told to wait two days for a ticket search is told something no caller should act on.
        var cw = new ScriptedConnectWise { FailingRoute = ("/service/tickets", HttpStatusCode.TooManyRequests), FailingRouteRetryAfter = TimeSpan.FromDays(2) };
        using var host = HostWith(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);

        using var response = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Equal("86400", body.GetProperty("retry_after_seconds").GetRawText());
    }

    [Fact]
    public async Task NoQueryAtAllListsRecentTickets()
    {
        // The picker on focus (Spec §5 S3): nothing typed is not too short, it is a different question.
        var cw = new ScriptedConnectWise();
        using var host = HostWith(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);

        using var response = await client.GetAsync(new Uri("/v1/psa/tickets", UriKind.Relative), TestContext.Current.CancellationToken);
        var found = await response.Content.ReadFromJsonAsync<TicketSearchResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["48213", "48190"], found!.Tickets.Select(t => t.Id));
        Assert.Contains("_info/lastUpdated", Uri.UnescapeDataString(Assert.Single(cw.Requests).RequestUri!.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooShortAQueryIsRefusedBeforeAnyProviderIsAsked()
    {
        var cw = new ScriptedConnectWise();
        using var host = HostWith(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.GetAsync(new Uri("/v1/psa/tickets?q=pr", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(cw.Requests);
    }

    [Fact]
    public async Task ARevokedDeviceCannotSearchThePsa()
    {
        var cw = new ScriptedConnectWise();
        using var host = HostWith(cw);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        _ = await api.UseAsync(db => db.Devices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));

        using var search = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        using var recent = await client.GetAsync(new Uri("/v1/psa/tickets", UriKind.Relative), TestContext.Current.CancellationToken);
        using var tooShort = await client.GetAsync(new Uri("/v1/psa/tickets?q=pr", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, recent.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tooShort.StatusCode);
        Assert.Empty(cw.Requests);
    }

    private WebApplicationFactoryHost HostWith(ScriptedConnectWise cw) => new(api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
            nameof(ConnectWiseProvider),
            options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw)));
    }));

    private static HttpClient Client(WebApplicationFactoryHost host, string token)
    {
        var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }

    /// <summary>Owns the derived factory so each test's handler is disposed with it.</summary>
    private sealed class WebApplicationFactoryHost(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) : IDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; } = factory;

        public void Dispose() => Factory.Dispose();
    }
}
