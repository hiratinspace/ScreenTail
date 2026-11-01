using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Demo;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Providers.Demo;

/// <summary>
/// The demo PSA (ST-090): in Development, with <c>Providers:Demo</c> on, a publish lands on a demo
/// ticket the owner can show an audience, under a name nobody can take for ConnectWise. Anywhere else the
/// service does not start with it, and without it nothing of the demo is registered or mapped.
/// </summary>
public sealed class DemoPsaTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly IPAddress Elsewhere = IPAddress.Parse("203.0.113.9");

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopmentTheSwitchStopsTheServiceFromStarting(string environment)
    {
        using var host = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseEnvironment(environment);
            _ = builder.UseSetting(DemoOptions.Setting, "true");
        });

        var thrown = Assert.ThrowsAny<Exception>(() => host.Services);

        Assert.Contains("Providers:Demo", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheSwitchOnInDevelopmentTheDemoIsThePsaAndTheKnowledgeBase()
    {
        using var host = Demo();
        using var scope = host.Services.CreateScope();

        Assert.IsType<DemoPsaProviderFactory>(scope.ServiceProvider.GetRequiredService<IPsaProviderFactory>());
        Assert.IsType<DemoDocProviderFactory>(scope.ServiceProvider.GetRequiredService<IDocProviderFactory>());
        Assert.NotNull(scope.ServiceProvider.GetService<DemoStore>());
    }

    [Fact]
    public async Task TheIntegrationsListNamesTheDemoAndNothingThatLooksReal()
    {
        // Settings → Integrations and the publish pane read this list. With the demo on, it is what a
        // publish reaches, so it is all the list says: a ConnectWise row would put a real product's name
        // on a note that went to the demo.
        using var host = Demo();
        var (tenantId, userId, deviceId) = await SeedAsync();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationVault>().StoreAsync(tenantId, "connectwise", "https://na.myconnectwise.net", "acme+PUB:PRIV-0001", TestContext.Current.CancellationToken);
        }

        using var client = Client(host, Token(tenantId, userId, deviceId));
        var listed = await client.GetFromJsonAsync<IntegrationsResponse>(new Uri("/v1/integrations", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal([DemoNames.Psa, DemoNames.Kb], listed!.Integrations.Select(i => i.Provider));
        Assert.All(listed.Integrations, i => Assert.Equal(string.Empty, i.Secret));
        Assert.Equal("Demo PSA (not ConnectWise)", DemoNames.PsaTitle);
        Assert.Equal("Demo knowledge base (not Hudu)", DemoNames.KbTitle);
    }

    [Theory]
    [InlineData("demo-psa")]
    [InlineData("demo-kb")]
    [InlineData("demo-anything")]
    public async Task NoCredentialCanBeStoredUnderADemoName(string provider)
    {
        // In any environment: a row stored as demo-psa would make a real tenant's integration look like
        // the demo in the client.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api, Token(tenantId, userId, deviceId));

        using var put = await client.PutAsJsonAsync(new Uri($"/v1/integrations/{provider}", UriKind.Relative), new StoreIntegrationRequest("https://example.com", "key-0001"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("reserved_provider", await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicketSearchFindsTheDentalPrinterTicket()
    {
        using var host = Demo();
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, Token(tenantId, userId, deviceId));

        var found = await client.GetFromJsonAsync<TicketSearchResponse>(new Uri("/v1/psa/tickets?q=printer", UriKind.Relative), TestContext.Current.CancellationToken);
        var recent = await client.GetFromJsonAsync<TicketSearchResponse>(new Uri("/v1/psa/tickets", UriKind.Relative), TestContext.Current.CancellationToken);
        var byNumber = await client.GetFromJsonAsync<TicketSearchResponse>(new Uri("/v1/psa/tickets?q=48213", UriKind.Relative), TestContext.Current.CancellationToken);

        var printer = Assert.Single(found!.Tickets);
        Assert.Equal(new TicketSearchRow("48213", "Printer offline in reception", "Acme Dental", "In Progress"), printer);
        Assert.InRange(recent!.Tickets.Count, 5, 10);
        Assert.Equal("48213", Assert.Single(byNumber!.Tickets).Id);
    }

    [Fact]
    public async Task APublishLandsOnTheDemoTicketAndTheReadEndpointShowsIt()
    {
        using var host = Demo(from: IPAddress.Loopback);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, Token(tenantId, userId, deviceId));
        var request = JsonNode.Parse(Contract())!;
        request["destinations"] = new JsonArray("ticket_note", "time_entry", "kb_article");

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var results = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString()));

        // No link: the client labels one "Open in ConnectWise", and this is not ConnectWise.
        Assert.All(results, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("link").ValueKind));

        using var anonymous = host.CreateClient();
        var shown = await anonymous.GetFromJsonAsync<JsonElement>(new Uri("/dev/demo-psa/tickets/48213", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(DemoNames.PsaTitle, shown.GetProperty("provider").GetString());
        Assert.Equal("Acme Dental", shown.GetProperty("company").GetString());
        var note = Assert.Single(shown.GetProperty("notes").EnumerateArray().ToList());
        Assert.StartsWith("Problem\nPrinter offline in reception.\n\nSteps\n1. ", note.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Drafted with ScreenTail, reviewed by A Technician.", note.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.True(note.GetProperty("internal").GetBoolean());
        Assert.Equal(1, note.GetProperty("screenshots").GetInt32());
        var entry = Assert.Single(shown.GetProperty("timeEntries").EnumerateArray().ToList());
        Assert.Equal(30, entry.GetProperty("minutes").GetInt32());

        var store = host.Services.GetRequiredService<DemoStore>();
        var article = Assert.Single(store.Articles());
        Assert.Equal("Acme Dental", article.Company);
        Assert.True(article.Draft);
    }

    [Fact]
    public async Task TheTicketPageShowsTheNoteAsTextUnderTheDemoName()
    {
        using var host = Demo(from: IPAddress.IPv6Loopback);
        var store = host.Services.GetRequiredService<DemoStore>();
        _ = store.AddNote(new TicketNote("48213", "Problem\nThe spooler had stopped. <script>alert(1)</script>", Internal: true, []));

        using var anonymous = host.CreateClient();
        using var page = await anonymous.GetAsync(new Uri("/dev/demo-psa/tickets/48213/view", UriKind.Relative), TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var index = await anonymous.GetAsync(new Uri("/dev/demo-psa", UriKind.Relative), TestContext.Current.CancellationToken);
        var list = await index.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Demo PSA (not ConnectWise)", html, StringComparison.Ordinal);
        Assert.Contains("Printer offline in reception", html, StringComparison.Ordinal);
        Assert.Contains("The spooler had stopped.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);

        // The page's own policy, which lets its inline styles in, and not the API's closed default laid
        // over it (SecurityHeaders): without style-src the demo page is drawn unstyled. The other
        // security headers are still added.
        var policy = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("style-src 'unsafe-inline'", policy, StringComparison.Ordinal);
        Assert.Contains("style-src 'unsafe-inline'", index.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("/dev/demo-psa/tickets/48213/view", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDemoPagesAnswerOnlyThisMachine()
    {
        // No token, so the address is the control: a caller from anywhere else sees no such page.
        using var host = Demo(from: Elsewhere);
        using var anonymous = host.CreateClient();

        foreach (var path in new[] { "/dev/demo-psa", "/dev/demo-psa/tickets/48213", "/dev/demo-psa/tickets/48213/view" })
        {
            using var response = await anonymous.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.5", "X-Forwarded-For", "127.0.0.1")]
    [InlineData("192.168.1.20", "192.168.1.5", "X-Forwarded-For", "::1")]
    [InlineData("192.168.1.20", "127.0.0.1", "X-Forwarded-For", "127.0.0.1")]
    [InlineData("203.0.113.9", "127.0.0.1", "X-Forwarded-For", "::1")]
    public async Task AForwardedForThatClaimsThisMachineIsRefusedEvenWhenTheFrameworkBelievesIt(string peer, string listener, string header, string claimed)
    {
        // Review 2026-10-27. With ASPNETCORE_FORWARDEDHEADERS_ENABLED=true the framework trusts every
        // proxy, so a caller on the LAN that says it is 127.0.0.1 becomes 127.0.0.1 before the page is
        // asked. The forwarding middleware runs here exactly that way, and the page still says 404.
        using var host = Demo(from: IPAddress.Parse(peer), at: IPAddress.Parse(listener), trustForwarding: true);
        using var anonymous = host.CreateClient();

        foreach (var path in new[] { "/dev/demo-psa", "/dev/demo-psa/tickets/48213", "/dev/demo-psa/tickets/48213/view" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add(header, claimed);
            using var response = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-Host", "localhost")]
    [InlineData("Forwarded", "for=127.0.0.1")]
    [InlineData("X-Original-For", "127.0.0.1:50000")]
    [InlineData("X-Original-Host", "localhost")]
    [InlineData("X-Real-IP", "127.0.0.1")]
    public async Task ARequestThatCameThroughAProxyIsRefusedEvenFromThisMachine(string header, string value)
    {
        // A proxy or tunnel on the laptop (nginx, ngrok, cloudflared) connects from loopback on behalf of
        // somebody else. Whatever it says about them, it says it in one of these, and a browser opened on
        // the laptop never sends any.
        using var host = Demo(from: IPAddress.Loopback);
        using var anonymous = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/dev/demo-psa/tickets/48213", UriKind.Relative));
        request.Headers.Add(header, value);

        using var response = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ALoopbackPeerOnAListenerThatIsNotLoopbackIsRefused()
    {
        using var host = Demo(from: IPAddress.Loopback, at: IPAddress.Parse("192.168.1.5"));
        using var anonymous = host.CreateClient();

        using var response = await anonymous.GetAsync(new Uri("/dev/demo-psa/tickets/48213", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("localhost:5001", HttpStatusCode.OK)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("127.0.0.1:5001", HttpStatusCode.OK)]
    [InlineData("[::1]:5001", HttpStatusCode.OK)]
    [InlineData("attacker.example", HttpStatusCode.NotFound)]
    [InlineData("rebind.attacker.example:5001", HttpStatusCode.NotFound)]
    [InlineData("192.168.1.5:5001", HttpStatusCode.NotFound)]
    [InlineData("localhost.attacker.example", HttpStatusCode.NotFound)]
    public async Task OnlyALocalHostNameIsAnswered(string hostHeader, HttpStatusCode expected)
    {
        // DNS rebinding: a page opened during the demo points its own name at 127.0.0.1 and reads the
        // JSON from the browser. Its requests still carry its own name, so a name that is not this
        // machine's is not answered.
        using var host = Demo(from: IPAddress.Loopback);
        using var anonymous = host.CreateClient();
        using var response = await anonymous.GetAsync(new Uri($"http://{hostHeader}/dev/demo-psa/tickets/48213"), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task TheJsonIsNeitherCachedNorSniffed()
    {
        using var host = Demo(from: IPAddress.Loopback);
        using var anonymous = host.CreateClient();

        using var response = await anonymous.GetAsync(new Uri("/dev/demo-psa/tickets/48213", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task WhileTheDemoIsOnAnArticleNeitherReadsNorWritesTheTenantsMappings()
    {
        // Review 2026-10-27. A real, hand-confirmed mapping of Acme Dental to a Hudu company is not the
        // demo's company, so the publish found it stale and deleted it. The demo matches in memory and
        // leaves the table as it was.
        using var host = Demo();
        var (tenantId, userId, deviceId) = await SeedAsync();
        _ = await MapByHandAsync(tenantId);
        var before = await MappingsOfAsync(tenantId);
        using var client = Client(host, Token(tenantId, userId, deviceId));
        var request = JsonNode.Parse(Contract())!;
        request["destinations"] = new JsonArray("kb_article");

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var result = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("results").EnumerateArray().ToList());

        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal("demo-kb-1", Assert.Single(host.Services.GetRequiredService<DemoStore>().Articles()).CompanyId);
        Assert.Equal(["Acme Dental=hudu-77/Acme Dental Ltd/manual"], before);
        Assert.Equal(before, await MappingsOfAsync(tenantId));
    }

    [Fact]
    public async Task WhileTheDemoIsOnACompanyTheDemoDoesNotHaveIsAskedAboutAndNothingIsWritten()
    {
        using var host = Demo();
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, Token(tenantId, userId, deviceId));
        var request = JsonNode.Parse(Contract())!;
        request["destinations"] = new JsonArray("kb_article");
        request["company"] = "Somebody Else";

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/publish", UriKind.Relative),
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var result = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("results").EnumerateArray().ToList());

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("needs_mapping", result.GetProperty("kind").GetString());
        Assert.Empty(host.Services.GetRequiredService<DemoStore>().Articles());
        Assert.Empty(await MappingsOfAsync(tenantId));
    }

    [Fact]
    public async Task WhileTheDemoIsOnTheTenantsMappingsAndCredentialsCannotBeChanged()
    {
        // The demo is what Settings shows while it is on, so a change made there would land on the
        // tenant's real rows unseen: a demo company over a real mapping, or a mapping or credential gone.
        using var host = Demo();
        var (tenantId, userId, deviceId) = await SeedAsync();
        _ = await MapByHandAsync(tenantId);
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationVault>().StoreAsync(tenantId, "hudu", "https://acme.huducloud.com", "hudu-key-0001", TestContext.Current.CancellationToken);
        }

        var before = await MappingsOfAsync(tenantId);
        using var client = Client(host, Token(tenantId, userId, deviceId));

        using var map = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), new MapCompanyRequest("Acme Dental", "demo-kb-1"), TestContext.Current.CancellationToken);
        using var unmap = await client.DeleteAsync(new Uri("/v1/integrations/hudu/companies/Acme%20Dental", UriKind.Relative), TestContext.Current.CancellationToken);
        using var store = await client.PutAsJsonAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), new StoreIntegrationRequest("https://other.huducloud.com", "hudu-key-0002"), TestContext.Current.CancellationToken);
        using var remove = await client.DeleteAsync(new Uri("/v1/integrations/hudu", UriKind.Relative), TestContext.Current.CancellationToken);
        var listed = await client.GetFromJsonAsync<CompanyMappingsResponse>(new Uri("/v1/integrations/hudu/companies", UriKind.Relative), TestContext.Current.CancellationToken);

        foreach (var refused in new[] { map, unmap, store, remove })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("demo_on", body, StringComparison.Ordinal);
            Assert.Contains("Providers:Demo", body, StringComparison.Ordinal);
        }

        Assert.Equal(before, await MappingsOfAsync(tenantId));
        Assert.Equal("https://acme.huducloud.com", await api.UseAsync(db => db.Integrations.Where(i => i.TenantId == tenantId && i.Provider == "hudu").Select(i => i.SiteUrl).SingleAsync()));
        Assert.Equal(["Acme Dental", "Borough Legal", "Harbour Vets"], listed!.Companies.Select(c => c.Name));
        Assert.Empty(listed.Mappings);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("203.0.113.9", false)]
    public void OnlyALoopbackAddressIsThisMachine(string address, bool local)
    {
        Assert.Equal(local, DemoPsaEndpoint.FromThisMachine(IPAddress.Parse(address)));
    }

    [Fact]
    public void ACallerWithNoAddressIsNotThisMachine()
    {
        Assert.False(DemoPsaEndpoint.FromThisMachine(null));
    }

    [Fact]
    public void WithTheSwitchOffNothingOfTheDemoIsRegisteredOrMapped()
    {
        // The fixture is Development with the switch unset: what a developer runs every day.
        using var scope = api.Services.CreateScope();

        Assert.IsType<ConnectWiseProviderFactory>(scope.ServiceProvider.GetRequiredService<IPsaProviderFactory>());
        Assert.IsType<HuduProviderFactory>(scope.ServiceProvider.GetRequiredService<IDocProviderFactory>());
        Assert.Null(scope.ServiceProvider.GetService<DemoStore>());
        Assert.Empty(DemoRoutes(api));
    }

    [Fact]
    public void InProductionNothingOfTheDemoIsRegisteredOrMapped()
    {
        using var production = api.WithWebHostBuilder(builder => builder.UseEnvironment(Environments.Production));
        using var scope = production.Services.CreateScope();

        Assert.IsType<ConnectWiseProviderFactory>(scope.ServiceProvider.GetRequiredService<IPsaProviderFactory>());
        Assert.IsType<HuduProviderFactory>(scope.ServiceProvider.GetRequiredService<IDocProviderFactory>());
        Assert.Null(scope.ServiceProvider.GetService<DemoStore>());
        Assert.Empty(DemoRoutes(production));
    }

    [Fact]
    public void WithTheSwitchOnTheDemoRoutesAreTheOnlyOnesOutsideV1AddedForIt()
    {
        using var host = Demo();

        Assert.Equal(
            ["/dev/demo-psa/", "/dev/demo-psa/tickets/{id}", "/dev/demo-psa/tickets/{id}/view"],
            DemoRoutes(host).Order(StringComparer.Ordinal));
    }

    private static List<string> DemoRoutes(WebApplicationFactory<Program> host) =>
        [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Where(p => p.StartsWith("/dev", StringComparison.Ordinal))];

    /// <summary>
    /// Development with the demo switched on, and every request arriving from <paramref name="from"/> at
    /// <paramref name="at"/> (loopback unless said). With <paramref name="trustForwarding"/>, the forwarding
    /// middleware runs next and trusts every proxy, as <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> does.
    /// </summary>
    private WebApplicationFactory<Program> Demo(IPAddress? from = null, IPAddress? at = null, bool trustForwarding = false) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting(DemoOptions.Setting, "true");
        if (from is not null)
        {
            _ = builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new ArrivingFrom(from, at ?? IPAddress.Loopback, trustForwarding)));
        }
    });

    private async Task<Guid> MapByHandAsync(Guid tenantId) => await api.UseAsync(async db =>
    {
        var id = Guid.NewGuid();
        db.CompanyMappings.Add(new CompanyMapping { Id = id, TenantId = tenantId, PsaCompany = "Acme Dental", DocCompanyId = "hudu-77", DocCompanyName = "Acme Dental Ltd", Confidence = "manual", CreatedAt = DateTimeOffset.UnixEpoch });
        _ = await db.SaveChangesAsync();
        return id;
    });

    private Task<List<string>> MappingsOfAsync(Guid tenantId) => api.UseAsync(db => db.CompanyMappings
        .Where(m => m.TenantId == tenantId)
        .Select(m => m.PsaCompany + "=" + m.DocCompanyId + "/" + m.DocCompanyName + "/" + m.Confidence)
        .ToListAsync());

    private static HttpClient Client(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string Token(Guid tenantId, Guid userId, Guid deviceId) => api.Issuer.ForDevice(tenantId, userId, deviceId).Token;

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

    /// <summary>The publish contract's own request: what the client posts, for the dental printer ticket.</summary>
    private static string Contract()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.publish-request.v1.json")
            ?? throw new InvalidOperationException("The publish contract fixture is not embedded in this assembly.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("request").GetRawText();
    }

    /// <summary>The test server says nothing about where a request came from or arrived; this says it, first thing.</summary>
    private sealed class ArrivingFrom(IPAddress remote, IPAddress local, bool trustForwarding) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            _ = app.Use((HttpContext context, RequestDelegate following) =>
            {
                context.Connection.RemoteIpAddress = remote;
                context.Connection.LocalIpAddress = local;
                return following(context);
            });
            if (trustForwarding)
            {
                var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.All };
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                _ = app.UseForwardedHeaders(options);
            }

            next(app);
        };
    }
}

/// <summary>The contract every PSA follows, run against the demo. Failures come from a wrapper: the demo itself never fails on purpose.</summary>
public sealed class DemoPsaMeetsTheContract : PsaProviderContract
{
    private readonly Breakable _provider = new(new DemoPsaProvider(new DemoStore(TimeProvider.System)));

    protected override IPsaProvider Provider => _provider;

    protected override string KnownTicketId => "48213";

    protected override void Break(ProviderError? error) => _provider.Fail = error;

    private sealed class Breakable(IPsaProvider inner) : IPsaProvider
    {
        public ProviderError? Fail { get; set; }

        public string Name => inner.Name;

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<bool>(error)) : inner.CheckAsync(ct);

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(error)) : inner.SearchTicketsAsync(query, ct);

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<IReadOnlyList<TicketRef>>(error)) : inner.RecentTicketsAsync(ct);

        public Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<PublishedNote>(error)) : inner.AddNoteAsync(note, ct);

        public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<PublishedNote>(error)) : inner.AddTimeEntryAsync(entry, ct);
    }
}

/// <summary>The contract every documentation platform follows, run against the demo knowledge base.</summary>
public sealed class DemoDocsMeetTheContract : DocProviderContract
{
    private readonly Breakable _provider = new(new DemoDocProvider(new DemoStore(TimeProvider.System)));

    protected override IDocProvider Provider => _provider;

    protected override string KnownCompanyId => "demo-kb-1";

    protected override void Break(ProviderError? error) => _provider.Fail = error;

    private sealed class Breakable(IDocProvider inner) : IDocProvider
    {
        public ProviderError? Fail { get; set; }

        public string Name => inner.Name;

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<bool>(error)) : inner.CheckAsync(ct);

        public Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<IReadOnlyList<CompanyRef>>(error)) : inner.ListCompaniesAsync(ct);

        public Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default) =>
            Fail is { } error ? Task.FromResult(ProviderResult.Failure<PublishedArticle>(error)) : inner.PublishArticleAsync(article, ct);
    }
}
