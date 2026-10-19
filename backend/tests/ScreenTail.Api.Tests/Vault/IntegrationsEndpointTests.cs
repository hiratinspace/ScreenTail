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
    public async Task AnotherTenantCannotRemoveOrReplaceMyCredential()
    {
        // 2026-10-12 review, T-189. The other tenant names the same provider: its DELETE finds nothing of
        // its own and says so, and its PUT makes a row of its own. Mine is untouched by either, byte for
        // byte, and still opens to my secret.
        var (tenantId, userId, deviceId) = await SeedAsync();
        var (otherTenant, otherUser, otherDevice) = await SeedAsync();
        using var mine = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var theirs = Client(api.Issuer.ForDevice(otherTenant, otherUser, otherDevice).Token);
        using var put = await mine.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://acme.huducloud.com", Secret), TestContext.Current.CancellationToken);
        var before = await api.UseAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == tenantId));

        using var removed = await theirs.DeleteAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), TestContext.Current.CancellationToken);
        using var replaced = await theirs.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://elsewhere.huducloud.com", "their-key-0000"), TestContext.Current.CancellationToken);
        var after = await api.UseAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == tenantId));
        using var scope = api.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IIntegrationVault>();

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("https://acme.huducloud.com", after.SiteUrl);
        Assert.Equal(before.SecretCiphertext, after.SecretCiphertext);
        Assert.Equal(before.DataKeyWrapped, after.DataKeyWrapped);
        Assert.Equal(before.ConnectedAt, after.ConnectedAt);
        Assert.Equal(Secret, (await vault.RevealAsync(tenantId, "hudu", TestContext.Current.CancellationToken))?.Secret);
        Assert.Equal("their-key-0000", (await vault.RevealAsync(otherTenant, "hudu", TestContext.Current.CancellationToken))?.Secret);
    }

    [Fact]
    public async Task RemovingWhatWasNeverConnectedIsNotFound()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var removed = await client.DeleteAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
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

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://127.1.2.3:8443")]
    [InlineData("https://2130706433")]
    [InlineData("https://0x7f.0.0.1")]
    [InlineData("https://localhost")]
    [InlineData("https://LOCALHOST.:8443")]
    [InlineData("https://psa.localhost")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://172.16.4.4")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::]")]
    [InlineData("https://[fd00::1]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://[::ffff:127.0.0.1]")]
    [InlineData("https://[::ffff:169.254.169.254]")]
    public async Task ASiteOnThisMachineOrAPrivateNetworkIsRefusedWhenStored(string site)
    {
        // Security check 2026-10-14: the backend calls the site a tenant stores, so a site that is
        // plainly internal is refused here, in words, before anything is kept. A public name that answers
        // with a private address is refused when the call connects (ProviderCallsStayPublicTests).
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/hudu", UriKind.Relative),
            new StoreIntegrationRequest(site, "hudu-key-0001"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("private_site_url", body, StringComparison.Ordinal);
        Assert.Contains("public", body, StringComparison.Ordinal);
        Assert.Empty(list!.Integrations);
    }

    [Theory]
    [InlineData("http://na.myconnectwise.net")]
    [InlineData("HTTP://na.myconnectwise.net")]
    [InlineData("ftp://na.myconnectwise.net")]
    [InlineData("ws://na.myconnectwise.net")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//na.myconnectwise.net")]
    [InlineData("na.myconnectwise.net")]
    [InlineData("/v4_6_release/apis/3.0")]
    [InlineData("https://")]
    [InlineData("https://acme:hunter2@na.myconnectwise.net")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ACredentialIsNeverStoredForASiteThatIsNotHttps(string siteUrl)
    {
        // Boundary 3 (2026-10-12 review, T-177). The site is where the provider worker sends the
        // credential: a ConnectWise Basic-auth string or a Hudu key over plain http is a credential on
        // the wire. A site with a user and password in it is a second credential, stored in the clear
        // and shown in full on the card, so it is refused too.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var put = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/connectwise", UriKind.Relative),
            new StoreIntegrationRequest(siteUrl, Secret),
            TestContext.Current.CancellationToken);
        var body = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("bad_site_url", body, StringComparison.Ordinal);
        Assert.False(await api.UseAsync(db => db.Integrations.AnyAsync(i => i.TenantId == tenantId)));
    }

    [Fact]
    public async Task ARequestWithNoSiteAtAllIsRefusedTheSameWay()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var put = await client.PutAsync(
            new Uri("/v1/integrations/hudu", UriKind.Relative),
            new StringContent("{\"secret\":\"hudu-key-0001\"}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("bad_site_url", body, StringComparison.Ordinal);
        Assert.False(await api.UseAsync(db => db.Integrations.AnyAsync(i => i.TenantId == tenantId)));
    }

    [Fact]
    public async Task AnHttpsCredentialIsNotReplacedByOneForAPlainHttpSite()
    {
        // The replace path, which is the one an attacker with a device would use: the stored site and
        // secret stay exactly as they were.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        using var good = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("https://na.myconnectwise.net", Secret), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);

        using var downgrade = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest("http://na.myconnectwise.net", "other-secret-0000"), TestContext.Current.CancellationToken);
        var stored = await api.UseAsync(db => db.Integrations.SingleAsync(i => i.TenantId == tenantId));

        Assert.Equal(HttpStatusCode.BadRequest, downgrade.StatusCode);
        Assert.Equal("https://na.myconnectwise.net", stored.SiteUrl);
        Assert.Equal("9876", stored.SecretHint);
    }

    [Theory]
    [InlineData("http://na.myconnectwise.net")]
    [InlineData("https://acme:hunter2@na.myconnectwise.net")]
    [InlineData("na.myconnectwise.net")]
    public async Task TheVaultItselfRefusesASiteThatIsNotHttps(string siteUrl)
    {
        // The endpoint is one way in; the vault is the only way to a stored credential. Refusing here
        // too means the next caller, an operator command or an import, cannot forget the rule.
        var (tenantId, _, _) = await SeedAsync();
        using var scope = api.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IIntegrationVault>();

        _ = await Assert.ThrowsAsync<ArgumentException>(() => vault.StoreAsync(tenantId, "connectwise", siteUrl, Secret, TestContext.Current.CancellationToken));
        Assert.False(await api.UseAsync(db => db.Integrations.AnyAsync(i => i.TenantId == tenantId)));
    }

    [Fact]
    public async Task TheVaultStoresTheSiteItJudged()
    {
        // The rule forgives surrounding whitespace, so the stored value must be the trimmed one. A
        // provider builds its address by appending to the site, and a site that kept a trailing space
        // would send the credential to a path nobody meant, whichever caller of the vault wrote it.
        var (tenantId, _, _) = await SeedAsync();
        using var scope = api.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IIntegrationVault>();

        await vault.StoreAsync(tenantId, "connectwise", " \thttps://na.myconnectwise.net/  ", Secret, TestContext.Current.CancellationToken);
        var stored = await api.UseAsync(db => db.Integrations.SingleAsync(i => i.TenantId == tenantId));
        var revealed = await vault.RevealAsync(tenantId, "connectwise", TestContext.Current.CancellationToken);

        Assert.Equal("https://na.myconnectwise.net/", stored.SiteUrl);
        Assert.Equal("https://na.myconnectwise.net/", revealed?.SiteUrl);
    }

    [Theory]
    [InlineData("https://na.myconnectwise.net")]
    [InlineData("https://psa.acme-msp.example:8443")]
    [InlineData("https://8.8.8.8")]
    [InlineData("https://[2606:4700:4700::1111]")]
    public async Task APublicSiteIsStoredAsBefore(string site)
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PutAsJsonAsync(
            new Uri("/v1/integrations/connectwise", UriKind.Relative),
            new StoreIntegrationRequest(site, Secret),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("https://na.myconnectwise.net")]
    [InlineData("HTTPS://na.myconnectwise.net/")]
    [InlineData("https://acme.huducloud.com:8443")]
    [InlineData("  https://na.myconnectwise.net  ")]
    public async Task AnHttpsSiteIsStored(string siteUrl)
    {
        // The control: the rule refuses the scheme, not the spelling.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var put = await client.PutAsJsonAsync(new Uri("/v1/integrations/connectwise", UriKind.Relative), new StoreIntegrationRequest(siteUrl, Secret), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.True(await api.UseAsync(db => db.Integrations.AnyAsync(i => i.TenantId == tenantId)));
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
