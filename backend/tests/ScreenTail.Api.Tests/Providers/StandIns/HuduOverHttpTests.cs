using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Publish;

namespace ScreenTail.Api.Tests.Providers.StandIns;

/// <summary>
/// The real <c>HuduProvider</c>, built by the real factory from the vault, over a real socket to a
/// stand-in Hudu (ST-095 to ST-097), driven through the publish endpoint and the company-mapping
/// endpoints the client calls. A publish needs a PSA, so a stand-in ConnectWise is beside it.
///
/// What this proves is our side against the shapes we were given: the key header, the paging, the
/// article's wrapper and fields, the upload's form, the company mapping, and what we do with each kind
/// of answer. It cannot prove that Hudu agrees, above all that it honours <c>draft</c>; only the real
/// check in <c>docs/integrations/hudu.md</c> can.
/// </summary>
public sealed class HuduOverHttpTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly Uri CompanyMappings = new("/v1/integrations/hudu/companies", UriKind.Relative);

    [Fact]
    public async Task AnArticleLandsAsADraftUnderTheExactlyMatchedCompanyWithItsScreenshots()
    {
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();
        var bundle = StandInBackend.Bundle() with { Destinations = ["kb_article"], Frames = [StandInBackend.Frame("f-1"), StandInBackend.Frame("f-2")] };

        var published = await backend.PublishAsync(bundle);

        var result = Assert.Single(published.Results);
        Assert.True(result.Ok, result.Error);
        var article = Assert.Single(hudu.Articles);
        Assert.Equal(article.Id.ToString(CultureInfo.InvariantCulture), result.Id);
        Assert.Equal(article.Url, result.Link);
        Assert.Equal(7, article.CompanyId);
        Assert.True(article.Draft);
        Assert.Equal(bundle.Note.SuggestedTitle, article.Name);
        Assert.Equal(KbArticleFormatter.Html(bundle.Note, bundle.TicketId, 2), article.Content);
        Assert.Equal(["screenshot-1.jpg", "screenshot-2.jpg"], hudu.Uploads.Select(u => u.FileName).Order(StringComparer.Ordinal));
        Assert.All(hudu.Uploads, u => Assert.Equal(("Article", article.Id, "image/jpeg"), (u.UploadableType, u.UploadableId, u.ContentType)));

        // Twenty-eight companies at twenty-five a page: two pages and no third, then the article, then
        // its uploads. The key is in the header on every one and in no query string.
        var requests = hudu.Requests;
        Assert.Equal(
            ["GET /api/v1/companies?page=1&page_size=25", "GET /api/v1/companies?page=2&page_size=25", "POST /api/v1/articles", "POST /api/v1/uploads", "POST /api/v1/uploads"],
            requests.Select(r => $"{r.Method} {r.Path}{r.RawQuery}"));
        Assert.All(requests, r =>
        {
            Assert.Equal(hudu.ApiKey, r.Header("x-api-key"));
            Assert.DoesNotContain(hudu.ApiKey, r.RawQuery, StringComparison.Ordinal);
            Assert.Contains("application/json", r.Header("Accept"), StringComparison.Ordinal);
            Assert.Equal(200, r.Status);
        });
        Assert.Empty(cw.Requests);

        var mapping = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == backend.TenantId, TestContext.Current.CancellationToken));
        Assert.Equal(("Acme Dental", "7", "exact"), (mapping.PsaCompany, mapping.DocCompanyId, mapping.Confidence));
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ACompanyMappedByHandFilesTheArticleUnderACompanyOnTheSecondPage()
    {
        // ST-097: no exact match, so the publish asks once and writes nothing; the technician picks
        // Hudu's company 27 from the list, which is on the second page; the next publish files it there.
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();
        var bundle = StandInBackend.Bundle(company: "Harbour Physio Ltd") with { Destinations = ["kb_article"] };
        backend.Remember("Harbour Physio Ltd");

        var first = Assert.Single((await backend.PublishAsync(bundle)).Results);
        using var listed = await backend.Client.GetAsync(CompanyMappings, TestContext.Current.CancellationToken);
        var offered = await listed.Content.ReadFromJsonAsync<CompanyMappingsResponse>(TestContext.Current.CancellationToken);
        using var mapped = await backend.Client.PutAsJsonAsync(CompanyMappings, new MapCompanyRequest("Harbour Physio Ltd", "27"), TestContext.Current.CancellationToken);
        var second = Assert.Single((await backend.PublishAsync(bundle)).Results);

        Assert.False(first.Ok);
        Assert.Equal("needs_mapping", first.Kind);
        Assert.Equal(28, offered!.Companies.Count);
        Assert.Contains(offered.Companies, c => c.Id == "27" && c.Name == "Harbour Physiotherapy");
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
        Assert.True(second.Ok, second.Error);
        Assert.Equal(27, Assert.Single(hudu.Articles).CompanyId);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task AWrongHuduKeyIsRefusedInWordsAndNeverSentAgain()
    {
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu);
        var wrong = Guid.NewGuid().ToString("N");
        await backend.SignInAsync(huduKey: wrong);

        var published = await backend.PublishAsync(StandInBackend.Bundle() with { Destinations = ["ticket_note", "kb_article"] });

        var kb = published.Results.Single(r => r.Destination == "kb_article");
        Assert.False(kb.Ok);
        Assert.Equal("unauthenticated", kb.Kind);
        Assert.False(kb.Retryable);
        Assert.Equal("Hudu rejected the API key. Update it in Settings → Integrations.", kb.Error);
        var refused = Assert.Single(hudu.Requests);
        Assert.Equal((401, wrong), (refused.Status, refused.Header("x-api-key")));
        Assert.Empty(hudu.Articles);
        Assert.True(published.Results.Single(r => r.Destination == "ticket_note").Ok);
        backend.Remember(wrong);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ARateLimitedArticleIsSentAgainAndLandsOnce()
    {
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        hudu.Inject(IsArticlePost, new Reply(429, new { error = "Rate limit exceeded" }, new Dictionary<string, string> { ["Retry-After"] = "0" }));
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();

        var kb = Assert.Single((await backend.PublishAsync(StandInBackend.Bundle() with { Destinations = ["kb_article"] })).Results);

        Assert.True(kb.Ok, kb.Error);
        Assert.Equal([429, 200], hudu.Requests.Where(IsArticlePost).Select(r => r.Status));
        _ = Assert.Single(hudu.Articles);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task AnOutageOnTheCompanyListIsAskedAgain()
    {
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        hudu.Inject(r => r.Method == "GET" && r.Path == "/api/v1/companies", new Reply(503, new { error = "Service unavailable" }));
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();

        var kb = Assert.Single((await backend.PublishAsync(StandInBackend.Bundle() with { Destinations = ["kb_article"] })).Results);

        Assert.True(kb.Ok, kb.Error);
        Assert.Equal([503, 200, 200], hudu.Requests.Where(r => r.Path == "/api/v1/companies").Select(r => r.Status));
    }

    [Fact]
    public async Task ACompanyRemovedSinceTheListWasCachedAsksForAnotherCompany()
    {
        // The list is cached ten minutes. A company removed in Hudu inside that window still passes our
        // check, and Hudu answers that the company must exist. That is the cache out of date, not a
        // ScreenTail bug: the mapping to it is forgotten, the list is asked of Hudu again, and the
        // technician is asked to choose another company.
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();
        var bundle = StandInBackend.Bundle() with { Destinations = ["kb_article"] };

        Assert.True(Assert.Single((await backend.PublishAsync(bundle)).Results).Ok);
        hudu.RemoveCompany(7);
        var kb = Assert.Single((await backend.PublishAsync(bundle)).Results);

        Assert.False(kb.Ok);
        Assert.Equal("needs_mapping", kb.Kind);
        Assert.False(kb.Retryable);
        Assert.Equal("The documentation platform no longer has \"Acme Dental\", so the mapping was forgotten and nothing was written. \"Acme Dental\" is not mapped to a company in the documentation platform. Map it in Settings → Integrations, then publish the article again.", kb.Error);
        Assert.Equal(2, hudu.Requests.Count(r => r.Path == "/api/v1/companies"));
        Assert.Equal([200, 422], hudu.Requests.Where(IsArticlePost).Select(r => r.Status));
        _ = Assert.Single(hudu.Articles);

        // What the mapping prompt then lists comes from Hudu, not the cache, and nothing maps to 7.
        var prompt = await backend.Client.GetFromJsonAsync<CompanyMappingsResponse>(CompanyMappings, TestContext.Current.CancellationToken);
        Assert.True(hudu.Requests.Count(r => r.Path == "/api/v1/companies") > 2, "The prompt's list came from the cache that still held the removed company.");
        Assert.DoesNotContain(prompt!.Companies, c => c.Id == "7");
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == backend.TenantId, TestContext.Current.CancellationToken)));
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task EveryDestinationAtOnceLandsWhereItShouldAndLeavesNothingInTheLog()
    {
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle() with
        {
            Destinations = ["ticket_note", "time_entry", "kb_article"],
            Frames = [StandInBackend.Frame("f-1"), StandInBackend.Frame("f-2"), StandInBackend.Frame("f-3")],
        });

        Assert.All(published.Results, r => Assert.True(r.Ok, $"{r.Destination}: {r.Error}"));
        Assert.Equal(["ticket_note", "time_entry", "kb_article"], published.Results.Select(r => r.Destination));
        _ = Assert.Single(cw.Notes);
        _ = Assert.Single(cw.TimeEntries);
        Assert.Equal(3, cw.Documents.Count);
        _ = Assert.Single(hudu.Articles);
        Assert.Equal(3, hudu.Uploads.Count);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    private static async Task<HuduStandIn> StartAsync()
    {
        var hudu = new HuduStandIn();
        await hudu.StartAsync();
        return hudu;
    }

    private static bool IsArticlePost(RecordedRequest r) => r.Method == "POST" && r.Path == "/api/v1/articles";
}
