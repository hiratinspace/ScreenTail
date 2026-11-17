using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Tests.Providers.ConnectWise;
using ScreenTail.Api.Tests.Providers.Hudu;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// Settings → Integrations' "Test connection" (ST-082): <c>POST /v1/integrations/{provider}/check</c>
/// asks the provider the cheapest thing that proves the credential, answers in words either way, and
/// records when it was last checked and what went wrong, so the card can say so before anyone publishes.
/// </summary>
public sealed class IntegrationCheckTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task ACheckAgainstConnectWiseSaysConnectedAndRecordsWhen()
    {
        var cw = new ScriptedConnectWise();
        using var host = HostWith(cw, new ScriptedHudu());
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.StoreIntegrationAsOwnerAsync(api, "connectwise", new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"));

        using var response = await client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var check = await response.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(check!.Ok);
        Assert.Equal("Connected to https://na.myconnectwise.net.", check.Message);
        Assert.EndsWith("/system/info", Assert.Single(cw.Requests).RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var row = Assert.Single(list!.Integrations);
        Assert.NotNull(row.LastCheckedAt);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task AWrongKeyIsAnAnswerInTheProvidersWordsNotAnError()
    {
        var cw = new ScriptedConnectWise { Failing = HttpStatusCode.Unauthorized };
        using var host = HostWith(cw, new ScriptedHudu());
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.StoreIntegrationAsOwnerAsync(api, "connectwise", new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:WRONG-0000"));

        using var response = await client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var check = await response.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(check!.Ok);
        Assert.Contains("web admin", check.Message, StringComparison.Ordinal);
        Assert.Equal(check.Message, Assert.Single(list!.Integrations).LastError);
    }

    [Theory]
    [InlineData("connectwise", "ConnectWise")]
    [InlineData("hudu", "Hudu")]
    public async Task AWrongKeyAsksTheTechnicianToAskTheOwnerAndTellsTheOwnerWhereToFixIt(string provider, string name)
    {
        // Integration credentials are the owner's (2026-11-15): a technician is never sent to Settings
        // to fix a key, and the owner, reading the same check on the web admin's card, is not told to ask
        // the owner but where to fix it.
        var cw = new ScriptedConnectWise { Failing = HttpStatusCode.Unauthorized };
        var hudu = new ScriptedHudu { Failing = HttpStatusCode.Unauthorized };
        using var host = HostWith(cw, hudu);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = provider == "connectwise"
            ? await client.StoreIntegrationAsOwnerAsync(api, provider, new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:WRONG-0000"))
            : await client.StoreIntegrationAsOwnerAsync(api, provider, new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-wrong-0000"));
        using var owner = await host.Factory.OwnerClientAsync(api, tenantId);

        using var deviceCheck = await client.PostAsync(new Uri($"/v1/integrations/{provider}/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var asDevice = await deviceCheck.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        using var ownerCheck = await owner.PostAsync(new Uri($"/v1/admin/integrations/{provider}/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var asOwner = await ownerCheck.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        var ownerList = await owner.GetFromJsonAsync<AdminIntegrationsResponse>(new Uri("/v1/admin/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal($"{name} rejected the API key. Ask your ScreenTail owner to update the {name} connection in the web admin.", asDevice!.Message);
        Assert.Equal($"{name} rejected the API key. Update the {name} connection with Replace on this card.", asOwner!.Message);
        Assert.Equal(asOwner.Message, ownerList!.Integrations.Single(i => i.Provider == provider).LastError);
        Assert.All([asDevice.Message, asOwner.Message], m => Assert.DoesNotContain("Settings", m, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAClientIdTheDeviceIsToldWhoFinishesTheSetupAndOnlyTheOwnerSeesTheSetting()
    {
        // Owner decision 2026-11-16: a technician never reads a configuration key. They are told to ask
        // the ScreenTail owner to finish the ConnectWise setup; the owner, on the card, is told the setting.
        var cw = new ScriptedConnectWise();
        using var host = HostWith(cw, new ScriptedHudu(), clientId: string.Empty);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.StoreIntegrationAsOwnerAsync(api, "connectwise", new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"));
        using var owner = await host.Factory.OwnerClientAsync(api, tenantId);

        using var deviceCheck = await client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var asDevice = await deviceCheck.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        using var tickets = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var ticketsBody = await tickets.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        using var ownerCheck = await owner.PostAsync(new Uri("/v1/admin/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var asOwner = await ownerCheck.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);

        Assert.Empty(cw.Requests);
        Assert.Equal("ConnectWise is not fully set up yet. Ask your ScreenTail owner to finish the ConnectWise setup.", asDevice!.Message);
        Assert.Contains("Ask your ScreenTail owner to finish the ConnectWise setup.", ticketsBody, StringComparison.Ordinal);
        Assert.All([asDevice.Message, ticketsBody, Assert.Single(list!.Integrations).LastError!], text =>
        {
            Assert.DoesNotContain("ClientId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ConnectWise:", text, StringComparison.Ordinal);
        });
        Assert.Equal("ConnectWise is not fully set up yet. Set ConnectWise:ClientId on the ScreenTail server, or ask whoever runs it to.", asOwner!.Message);
    }

    [Fact]
    public async Task ACredentialTheVaultRefusesIsAnsweredInWordsOnBothCheckRoutesAndWhileMapping()
    {
        // 2026-11-15 review: a row bound to another site does not open. Test connection says so (to a
        // technician: ask the owner; to the owner: what to do), records it, and nothing is a bare 500 (a
        // provider refusal is the endpoints' own 502 with the words in it).
        var cw = new ScriptedConnectWise();
        var hudu = new ScriptedHudu();
        using var host = HostWith(cw, hudu);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var storedCw = await client.StoreIntegrationAsOwnerAsync(api, "connectwise", new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"));
        using var storedHudu = await client.StoreIntegrationAsOwnerAsync(api, "hudu", new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-5678"));
        _ = await api.UseAsync(db => db.Integrations.Where(i => i.TenantId == tenantId).ExecuteUpdateAsync(set => set.SetProperty(i => i.SiteUrl, "https://moved.example.com")));
        using var owner = await host.Factory.OwnerClientAsync(api, tenantId);

        using var deviceCheck = await client.PostAsync(new Uri("/v1/integrations/hudu/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var asDevice = await deviceCheck.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        using var ownerCheck = await owner.PostAsync(new Uri("/v1/admin/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var asOwner = await ownerCheck.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        using var companies = await client.GetAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), TestContext.Current.CancellationToken);
        using var tickets = await client.GetAsync(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        var ownerList = await owner.GetFromJsonAsync<AdminIntegrationsResponse>(new Uri("/v1/admin/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, deviceCheck.StatusCode);
        Assert.False(asDevice!.Ok);
        Assert.Contains("Ask your ScreenTail owner to connect it again in the web admin.", asDevice.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ownerCheck.StatusCode);
        Assert.False(asOwner!.Ok);
        Assert.Contains("Connect it again with Replace on this card.", asOwner.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask your", asOwner.Message, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.InternalServerError, companies.StatusCode);
        Assert.Contains("Ask your ScreenTail owner", await companies.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.InternalServerError, tickets.StatusCode);
        Assert.Contains("Ask your ScreenTail owner", await tickets.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(cw.Requests);
        Assert.Empty(hudu.Requests);
        Assert.All(list!.Integrations, row => Assert.Contains("Ask your ScreenTail owner to connect it again", row.LastError, StringComparison.Ordinal));
        Assert.All(ownerList!.Integrations, row => Assert.DoesNotContain("Ask your", row.LastError ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHuduCheckAsksForItsApiInfo()
    {
        var hudu = new ScriptedHudu();
        using var host = HostWith(new ScriptedConnectWise(), hudu);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.StoreIntegrationAsOwnerAsync(api, "hudu", new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-5678"));

        using var response = await client.PostAsync(new Uri("/v1/integrations/hudu/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var check = await response.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);

        Assert.True(check!.Ok, check.Message);
        Assert.EndsWith("/api/v1/api_info", Assert.Single(hudu.Requests).RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingConnectedIsNotFound()
    {
        using var host = HostWith(new ScriptedConnectWise(), new ScriptedHudu());
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private WebApplicationFactoryHost HostWith(ScriptedConnectWise cw, ScriptedHudu hudu, string clientId = "11111111-2222-3333-4444-555555555555") => new(api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", clientId);
        _ = builder.ConfigureTestServices(services =>
        {
            _ = services.Configure<HttpClientFactoryOptions>(nameof(ConnectWiseProvider), options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = cw));
            _ = services.Configure<HttpClientFactoryOptions>(nameof(HuduProvider), options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = hudu));
        });
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

    private sealed class WebApplicationFactoryHost(WebApplicationFactory<Program> factory) : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;

        public void Dispose() => Factory.Dispose();
    }
}
