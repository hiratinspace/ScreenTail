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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
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

    /// <summary>Development with the demo switched on, and every request arriving from <paramref name="from"/>.</summary>
    private WebApplicationFactory<Program> Demo(IPAddress? from = null) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting(DemoOptions.Setting, "true");
        if (from is not null)
        {
            _ = builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new ArrivingFrom(from)));
        }
    });

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

    /// <summary>The test server says nothing about where a request came from; this says it, first thing.</summary>
    private sealed class ArrivingFrom(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            _ = app.Use((HttpContext context, RequestDelegate following) =>
            {
                context.Connection.RemoteIpAddress = address;
                return following(context);
            });
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
