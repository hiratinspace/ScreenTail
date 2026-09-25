using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Providers.Hudu;

/// <summary>Hudu's REST API as the provider speaks it (ST-095): what goes on the wire and what comes back.</summary>
public sealed class HuduProviderTests : IDisposable
{
    private static readonly string[] BlankName = ["can't be blank"];
    private readonly ScriptedHudu _api = new();
    private readonly AdjustableTime _time = new(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task EveryRequestCarriesTheKeyAsAHeaderNeverInTheUrl()
    {
        _ = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(_api.Requests);
        Assert.Equal("hudu-api-key-1234", Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.DoesNotContain("hudu-api-key", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/api/v1/api_info", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidKeyIsSaidInSoManyWords()
    {
        // ST-095 AC2.
        _api.Failing = HttpStatusCode.Unauthorized;

        var result = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Unauthenticated, result.Error!.Kind);
        Assert.Equal("Hudu rejected the API key.", result.Error.What);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task CompaniesArePagedUntilAShortPage()
    {
        // Hudu pages at 25. Twenty-eight companies are two requests, not one truncated list.
        var result = await Provider().ListCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(28, result.Value!.Count);
        Assert.Equal(2, _api.Requests.Count);
        Assert.Contains(result.Value, c => c.Id == "7" && c.Name == "Acme Dental");
    }

    [Fact]
    public async Task CompaniesAreCachedForTenMinutesPerTenant()
    {
        // ST-095 AC1. The list is asked for on every publish to check the company; Hudu does not need to
        // hear the same question twice in ten minutes, and the mapping screen will ask often.
        var cache = new HuduCompanyCache(_time);
        var first = Provider(cache);
        var second = Provider(cache);

        _ = await first.ListCompaniesAsync(TestContext.Current.CancellationToken);
        _ = await second.ListCompaniesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _api.Requests.Count);

        _time.Advance(TimeSpan.FromMinutes(11));
        _ = await second.ListCompaniesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, _api.Requests.Count);
    }

    [Fact]
    public async Task AnotherTenantsCacheIsNotThisOnes()
    {
        var cache = new HuduCompanyCache(_time);
        _ = await Provider(cache, tenant: "t-1").ListCompaniesAsync(TestContext.Current.CancellationToken);
        _ = await Provider(cache, tenant: "t-2").ListCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, _api.Requests.Count);
    }

    [Fact]
    public async Task AnArticleIsADraftUnderItsCompanyWithItsAttachmentsUploaded()
    {
        // ST-096 AC1 and the image-upload half of ST-095 AC3: the article is created as a draft under the
        // company, then each attachment is uploaded against it, so a reviewer finds the screenshots on
        // the article whether or not the body's image links resolve.
        var article = new KbArticle("7", "Printer offline", "Problem, steps, result.",
        [
            new NoteAttachment("screenshot-1.jpg", "image/jpeg", new byte[] { 1 }),
            new NoteAttachment("screenshot-2.jpg", "image/jpeg", new byte[] { 2 }),
        ]);

        var result = await Provider().PublishArticleAsync(article, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("5101", result.Value!.Id);
        Assert.Equal(new Uri("https://acme.huducloud.com/a/printer-offline-5101"), result.Value.Url);
        var create = _api.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
        var body = JsonDocument.Parse(_api.Bodies[_api.Requests.IndexOf(create)]!).RootElement.GetProperty("article");
        Assert.Equal("Printer offline", body.GetProperty("name").GetString());
        Assert.Equal(7, body.GetProperty("company_id").GetInt32());
        Assert.True(body.GetProperty("draft").GetBoolean());
        Assert.Contains("Problem, steps, result.", body.GetProperty("content").GetString(), StringComparison.Ordinal);
        var uploads = _api.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/uploads", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, uploads.Count);
        Assert.Contains("uploadable_type", _api.Bodies[_api.Requests.IndexOf(uploads[0])], StringComparison.Ordinal);
        Assert.Contains("5101", _api.Bodies[_api.Requests.IndexOf(uploads[0])], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArticleForACompanyHuduDoesNotHaveIsRefusedBeforeItIsSent()
    {
        // Publishing to the wrong company is a customer's runbook in another customer's knowledge base.
        var result = await Provider().PublishArticleAsync(new KbArticle("999", "Title", "Body", []), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.NotFound, result.Error!.Kind);
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/api/v1/articles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARejectedArticleCarriesHudusOwnWords()
    {
        // The company check comes first and is answered by the routes, then cached; only the create is
        // scripted, so the refusal lands on the article and not on the list.
        var provider = Provider();
        _ = await provider.ListCompaniesAsync(TestContext.Current.CancellationToken);
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = JsonContent.Create(new { errors = new { name = BlankName } }),
        });

        var result = await provider.PublishArticleAsync(new KbArticle("7", "", "Body", []), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Contains("can't be blank", result.Error.What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitBacksOffAndGivesUpAfterThree()
    {
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _api.Requests.Count);
        Assert.Equal(ProviderErrorKind.Unavailable, result.Error!.Kind);
    }

    /// <summary>A clock the test moves. Enough for a cache's expiry; nothing here needs timers.</summary>
    private sealed class AdjustableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private HuduProvider Provider(HuduCompanyCache? cache = null, string tenant = "t-1") =>
        new(
            new HttpClient(_api) { BaseAddress = new Uri("https://acme.huducloud.com/") },
            "hudu-api-key-1234",
            new HuduOptions { BackoffBase = TimeSpan.Zero },
            cache ?? new HuduCompanyCache(_time),
            tenant,
            _time);
}

/// <summary>The contract every documentation provider meets (ST-090), run against the recorded Hudu.</summary>
public sealed class HuduMeetsTheContract : DocProviderContract, IDisposable
{
    private readonly ScriptedHudu _api = new();
    private readonly HuduProvider _provider;

    public HuduMeetsTheContract()
    {
        _provider = new HuduProvider(
            new HttpClient(_api) { BaseAddress = new Uri("https://acme.huducloud.com/") },
            "hudu-api-key-1234",
            new HuduOptions { BackoffBase = TimeSpan.Zero },
            new HuduCompanyCache(TimeProvider.System),
            "t-contract",
            TimeProvider.System);
    }

    public void Dispose() => _api.Dispose();

    protected override IDocProvider Provider => _provider;

    protected override string KnownCompanyId => "7";

    protected override void Break(ProviderError? error) => _api.Failing = error is null ? null : ScriptedHudu.StatusFor(error.Kind);
}

/// <summary>The provider a tenant gets is built from the vault's <c>hudu</c> credential and nothing else.</summary>
public sealed class HuduProviderFactoryTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    [Fact]
    public async Task ATenantWithAKeyGetsAProviderPointedAtItsSite()
    {
        await using var db = await OpenAsync();
        var tenant = Guid.NewGuid();
        var vault = new IntegrationVault(db, Options(), TimeProvider.System);
        await vault.StoreAsync(tenant, "hudu", "https://acme.huducloud.com", "hudu-api-key-1234", TestContext.Current.CancellationToken);
        var factory = new HuduProviderFactory(vault, new OneClientFactory(), new HuduOptions(), new HuduCompanyCache(TimeProvider.System), TimeProvider.System);

        var provider = await factory.ForTenantAsync(tenant, TestContext.Current.CancellationToken);

        Assert.NotNull(provider);
        Assert.Equal("hudu", provider.Name);
    }

    [Fact]
    public async Task ATenantWithNoKeyGetsNothing()
    {
        await using var db = await OpenAsync();
        var factory = new HuduProviderFactory(new IntegrationVault(db, Options(), TimeProvider.System), new OneClientFactory(), new HuduOptions(), new HuduCompanyCache(TimeProvider.System), TimeProvider.System);

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
        public HttpClient CreateClient(string name) => new(new ScriptedHudu());
    }
}
