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
        // ConnectWise is asked one thing: whether it has the company, before the match is remembered.
        var lookup = Assert.Single(cw.Requests);
        Assert.Equal(("GET", $"{ConnectWiseStandIn.ApiRoot}company/companies", "name = 'Acme Dental'"), (lookup.Method, lookup.Path, lookup.Query["conditions"]));

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
    public async Task AMappingIsCheckedAgainstConnectWiseAndRefusedForACompanyItDoesNotHaveOrMayNotRead()
    {
        // ST-097: the PSA company is looked up by name over the socket, asking for id and name only, and
        // only a company ConnectWise has is mapped. A member that may not read companies refuses in words.
        await using var cw = await ConnectWiseOverHttpTests.StartAsync();
        await using var hudu = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu);
        await backend.SignInAsync();
        backend.Remember("Harbour Physio Ltd", "Harbour Physio Limited");

        using var unknown = await backend.Client.PutAsJsonAsync(CompanyMappings, new MapCompanyRequest("Harbour Physio Limited", "27"), TestContext.Current.CancellationToken);
        var unknownBody = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        _ = cw.Denied.Add("company/companies");
        using var denied = await backend.Client.PutAsJsonAsync(CompanyMappings, new MapCompanyRequest("Harbour Physio Ltd", "27"), TestContext.Current.CancellationToken);
        var deniedBody = await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        _ = cw.Denied.Remove("company/companies");
        using var mapped = await backend.Client.PutAsJsonAsync(CompanyMappings, new MapCompanyRequest("Harbour Physio Ltd", "27"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("no_such_psa_company", unknownBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadGateway, denied.StatusCode);
        Assert.Contains("psa_forbidden", deniedBody, StringComparison.Ordinal);
        Assert.Contains("ConnectWise says this API member may not do that.", deniedBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
        var lookups = cw.Requests.Where(r => r.Path.EndsWith("/company/companies", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, lookups.Count);
        Assert.Equal("name = 'Harbour Physio Ltd'", lookups[^1].Query["conditions"]);
        Assert.Equal("id,name", lookups[^1].Query["fields"]);
        var mapping = await api.UseAsync(db => db.CompanyMappings.SingleAsync(m => m.TenantId == backend.TenantId, TestContext.Current.CancellationToken));
        Assert.Equal(("Harbour Physio Ltd", "27"), (mapping.PsaCompany, mapping.DocCompanyId));
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
        Assert.Equal("Hudu rejected the API key. Ask your ScreenTail owner to update the Hudu connection in the web admin.", kb.Error);
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
