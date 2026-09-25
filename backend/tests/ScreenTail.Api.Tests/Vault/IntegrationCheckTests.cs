using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:PRIV-1234"), TestContext.Current.CancellationToken);

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
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", "acme+PUB:WRONG-0000"), TestContext.Current.CancellationToken);

        using var response = await client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var check = await response.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(check!.Ok);
        Assert.Contains("Settings", check.Message, StringComparison.Ordinal);
        Assert.Equal(check.Message, Assert.Single(list!.Integrations).LastError);
    }

    [Fact]
    public async Task AHuduCheckAsksForItsApiInfo()
    {
        var hudu = new ScriptedHudu();
        using var host = HostWith(new ScriptedConnectWise(), hudu);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://acme.huducloud.com", "hudu-key-5678"), TestContext.Current.CancellationToken);

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

    private WebApplicationFactoryHost HostWith(ScriptedConnectWise cw, ScriptedHudu hudu) => new(api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
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
