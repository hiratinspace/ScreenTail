using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// The PSA and documentation clients as the running service builds them, pointed at this machine
/// (ST-009, security check 2026-10-14). A tenant chooses the site its provider calls go to, so a site on
/// a private network would turn the backend into a way to reach and read from hosts nobody outside it
/// can. The store-time check catches the obvious spellings; this is the check that holds when the row is
/// already there, or when a public name answers with a private address.
///
/// Nothing here replaces the clients' handlers: what is under test is the handler Program.cs gives them.
/// </summary>
public sealed class ProviderCallsStayPublicTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData("connectwise", "127.0.0.1")]
    [InlineData("connectwise", "localhost")]
    [InlineData("hudu", "127.0.0.1")]
    [InlineData("hudu", "localhost")]
    public async Task AStoredSiteOnAPrivateAddressIsNeverCalled(string provider, string host)
    {
        using var listener = new LoopbackListener();
        using var service = Host(allowPrivate: false);
        var token = await StoreAsync(service, provider, $"https://{host}:{listener.Port}");
        using var client = Client(service, token);

        using var response = await client.PostAsync(new Uri($"/v1/integrations/{provider}/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var check = await response.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);

        // Counted once the listener has taken every connection made so far, rather than after a pause
        // a busy machine could outlast.
        Assert.Equal(0, await listener.ConnectionsBeforeNowAsync(TestContext.Current.CancellationToken));
        Assert.False(check!.Ok);
        Assert.Contains("private network", check.Message, StringComparison.Ordinal);
        Assert.Contains("Settings", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentCanReachALocalProviderWhenItSaysSoOutLoud()
    {
        // The control for the test above, and the local-development path: with the option on, the same
        // call does reach the port, so a zero there means refused and not merely missed.
        using var listener = new LoopbackListener();
        using var service = Host(allowPrivate: true);
        var token = await StoreAsync(service, "hudu", $"https://localhost:{listener.Port}");
        using var client = Client(service, token);

        using var response = await client.PostAsync(new Uri("/v1/integrations/hudu/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var check = await response.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);

        Assert.False(check!.Ok);
        Assert.DoesNotContain("private network", check.Message, StringComparison.Ordinal);
        Assert.True(listener.Accepted > 0);
    }

    [Fact]
    public async Task DevelopmentMayStoreALocalSiteWhenItSaysSoOutLoud()
    {
        using var service = Host(allowPrivate: true);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(service, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://localhost:8443", "hudu-key-0001"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, put.StatusCode);
    }

    [Fact]
    public void OutsideDevelopmentTheOptionStopsTheServiceFromStarting()
    {
        // Reaching private addresses is for a developer's machine and this suite. A deployment that has
        // it switched on has had its protection removed by a setting, so it does not start at all.
        using var production = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseEnvironment(Environments.Production);
            _ = builder.UseSetting("ProviderNetwork:AllowPrivateAddresses", "true");
        });

        var thrown = Assert.ThrowsAny<Exception>(() => production.Services);

        Assert.Contains("ProviderNetwork:AllowPrivateAddresses", thrown.ToString(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Host(bool allowPrivate) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("ConnectWise:ClientId", "11111111-2222-3333-4444-555555555555");
        _ = builder.UseSetting("ConnectWise:BackoffBase", "00:00:00");
        _ = builder.UseSetting("Hudu:BackoffBase", "00:00:00");
        _ = builder.UseSetting("ProviderNetwork:AllowPrivateAddresses", allowPrivate ? "true" : "false");
    });

    /// <summary>Straight into the vault, past the endpoint's own check: a row from before it, or one a public name now answers for with a private address.</summary>
    private async Task<string> StoreAsync(WebApplicationFactory<Program> service, string provider, string site)
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var scope = service.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationVault>().StoreAsync(tenantId, provider, site, "acme+PUB:PRIV-0001", TestContext.Current.CancellationToken);
        return api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
    }

    private static HttpClient Client(WebApplicationFactory<Program> service, string token)
    {
        var client = service.CreateClient();
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
}
