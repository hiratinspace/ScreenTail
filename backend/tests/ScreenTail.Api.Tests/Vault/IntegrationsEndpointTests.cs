using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// The integrations endpoints (ST-009): a credential goes in once, is never read back over HTTP, and is
/// ciphertext in the row. Workers get it through <see cref="IIntegrationVault"/>, in process.
/// </summary>
public sealed class IntegrationsEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Secret = "acme+PUBLICKEY:PRIVATE-SECRET-9876";

    [Fact]
    public async Task ACredentialGoesInAndOnlyItsLastFourComeBack()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var put = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/connectwise", UriKind.Relative),
            new StoreIntegrationRequest("https://na.myconnectwise.net", Secret),
            TestContext.Current.CancellationToken);
        using var get = await client.GetAsync(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var list = await get.Content.ReadFromJsonAsync<IntegrationsResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var row = Assert.Single(list!.Integrations);
        Assert.Equal("connectwise", row.Provider);
        Assert.Equal("https://na.myconnectwise.net", row.SiteUrl);
        Assert.Equal("••••9876", row.Secret);
        Assert.DoesNotContain("PRIVATE", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRowIsCiphertext()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/hudu", UriKind.Relative),
            new StoreIntegrationRequest("https://acme.huducloud.com", Secret),
            TestContext.Current.CancellationToken);

        var stored = await api.UseAsync(db => db.Integrations.SingleAsync(i => i.TenantId == tenantId && i.Provider == "hudu"));

        Assert.NotNull(stored.SecretCiphertext);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(Secret), stored.SecretCiphertext);
        Assert.Equal("9876", stored.SecretHint);
        Assert.NotNull(stored.KeyId);
        Assert.NotNull(stored.ConnectedAt);
    }

    [Fact]
    public async Task AWorkerCanRevealItAndHttpNeverDoes()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var put = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/connectwise", UriKind.Relative),
            new StoreIntegrationRequest("https://na.myconnectwise.net", Secret),
            TestContext.Current.CancellationToken);

        using var scope = api.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IIntegrationVault>();
        var revealed = await vault.RevealAsync(tenantId, "connectwise", TestContext.Current.CancellationToken);

        Assert.Equal(Secret, revealed?.Secret);
        Assert.Equal("https://na.myconnectwise.net", revealed?.SiteUrl);
        Assert.Null(await vault.RevealAsync(tenantId, "hudu", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnotherTenantSeesNothingAndRevealsNothing()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var (otherTenant, otherUser, otherDevice) = await SeedAsync();
        using var mine = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var theirs = Client(api.Issuer.ForDevice(otherTenant, otherUser, otherDevice).Token);
        using var put = await mine.PutAsJsonAsync(
            new Uri("/v1/integrations/connectwise", UriKind.Relative),
            new StoreIntegrationRequest("https://na.myconnectwise.net", Secret),
            TestContext.Current.CancellationToken);

        var list = await theirs.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        using var scope = api.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IIntegrationVault>();

        Assert.Empty(list!.Integrations);
        Assert.Null(await vault.RevealAsync(otherTenant, "connectwise", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplacingAndDeletingWork()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var first = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://a.huducloud.com", "old-key-0001"), TestContext.Current.CancellationToken);
        using var second = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://a.huducloud.com", "new-key-0002"), TestContext.Current.CancellationToken);

        var afterReplace = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        using var delete = await client.DeleteAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), TestContext.Current.CancellationToken);
        var afterDelete = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("••••0002", Assert.Single(afterReplace!.Integrations).Secret);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(afterDelete!.Integrations);
    }

    [Fact]
    public async Task AProviderNameThatIsNotANameIsRefused()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/Connect%20Wise!", UriKind.Relative),
            new StoreIntegrationRequest("https://x", "k"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WithoutATokenNothingIsReachable()
    {
        using var client = api.CreateClient();

        using var get = await client.GetAsync(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://x", "k"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    private HttpClient Client(string token)
    {
        var client = api.CreateClient();
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

/// <summary>The same endpoints with no master key configured: storing is refused with a reason, listing still works.</summary>
public sealed class UnconfiguredVaultTests(UnconfiguredVaultTests.NoVaultFixture api) : IClassFixture<UnconfiguredVaultTests.NoVaultFixture>
{
    [Fact]
    public async Task StoringIsRefusedWithAReasonAndListingIsEmpty()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://x", "k-0001"), TestContext.Current.CancellationToken);
        var problem = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, put.StatusCode);
        Assert.Contains("not_configured", problem, StringComparison.Ordinal);
        Assert.Empty(list!.Integrations);
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "t@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }

    public sealed class NoVaultFixture : ApiFixture
    {
        protected override string? VaultMasterKey => null;
    }
}
