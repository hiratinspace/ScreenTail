using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Publish;

namespace ScreenTail.Api.Tests.Providers.StandIns;

/// <summary>
/// The real <c>ConnectWiseProvider</c>, built by the real factory from the vault, over a real socket to
/// a stand-in ConnectWise (ST-091 to ST-094), driven through the endpoints the client calls: publish,
/// ticket search and Test connection. Each test reads what landed in the stand-in and what the provider
/// put on the wire, and checks the service's log for anything it sent (INV-10).
///
/// What this proves is our side against the shapes we were given: the routes, the headers, the field
/// names, what we do with each kind of answer. It cannot prove that ConnectWise agrees; only the sandbox
/// check in <c>docs/integrations/connectwise.md</c> can.
/// </summary>
public sealed class ConnectWiseOverHttpTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Ticket = "731946";

    [Theory]
    [InlineData("internal")]
    [InlineData("discussion")]
    public async Task TheNoteLandsOnTheTicketAsTheTypeChosenWithItsScreenshots(string noteType)
    {
        await using var cw = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();
        var bundle = StandInBackend.Bundle() with { NoteType = noteType, Destinations = ["ticket_note"], Frames = [StandInBackend.Frame("f-1"), StandInBackend.Frame("f-2")] };

        var published = await backend.PublishAsync(bundle);

        var result = Assert.Single(published.Results);
        Assert.True(result.Ok, result.Error);
        Assert.Null(result.Kind);
        var note = Assert.Single(cw.Notes);
        Assert.Equal(note.Id.ToString(CultureInfo.InvariantCulture), result.Id);
        Assert.Equal(int.Parse(Ticket, CultureInfo.InvariantCulture), note.TicketId);
        Assert.Equal(NoteFormatter.Render(bundle.Note, ["f-1", "f-2"], bundle.Reviewer, footer: true), note.Text);
        Assert.Equal(noteType == "internal", note.InternalAnalysis);
        Assert.Equal(noteType == "discussion", note.DetailDescription);
        Assert.False(note.Resolution);
        Assert.False(note.CustomerUpdated);

        // The screenshots are documents on the same ticket, private exactly when the note is, sent after
        // the note has its id.
        Assert.Equal(["screenshot-1.jpg", "screenshot-2.jpg"], cw.Documents.Select(d => d.FileName).Order(StringComparer.Ordinal));
        Assert.All(cw.Documents, d =>
        {
            Assert.Equal(note.TicketId, d.TicketId);
            Assert.Equal(noteType == "internal", d.Private);
            Assert.Equal("image/jpeg", d.ContentType);
            Assert.True(d.Length > 0);
        });
        var requests = cw.Requests;
        var notePost = Assert.Single(requests, r => r.Method == "POST" && r.Path == $"{ConnectWiseStandIn.ApiRoot}service/tickets/{Ticket}/notes");
        Assert.All(requests.Where(r => r.Path.EndsWith("/system/documents", StringComparison.Ordinal)), d => Assert.True(d.At >= notePost.At));

        AssertEveryRequestCarriesTheTenantsCredential(cw);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Theory]
    [InlineData("internal", true)]
    [InlineData("discussion", false)]
    public async Task TheTimeEntryIsTheWindowWorkedChargedToTheApiMember(string noteType, bool billable)
    {
        await using var cw = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();
        var bundle = StandInBackend.Bundle() with { NoteType = noteType, Billable = billable, Destinations = ["time_entry"] };

        var published = await backend.PublishAsync(bundle);

        var result = Assert.Single(published.Results);
        Assert.True(result.Ok, result.Error);
        var entry = Assert.Single(cw.TimeEntries);
        Assert.Equal(entry.Id.ToString(CultureInfo.InvariantCulture), result.Id);
        Assert.Equal(int.Parse(Ticket, CultureInfo.InvariantCulture), entry.TicketId);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero), entry.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 14, 32, 0, TimeSpan.Zero), entry.End);
        Assert.Equal(billable ? "Billable" : "DoNotBill", entry.BillableOption);

        // The text goes where the note type says: an internal note's words never reach an invoice.
        var text = NoteFormatter.Render(bundle.Note, [], bundle.Reviewer, footer: true);
        Assert.Equal(noteType == "internal" ? null : text, entry.Notes);
        Assert.Equal(noteType == "internal" ? text : null, entry.InternalNotes);
        Assert.All(entry.Flags, f => Assert.False(f.Value, f.Key));

        // No member is named, so the entry is the API member's: the per-technician member is ST-010's.
        Assert.Equal(cw.ApiMember, entry.Member);
        var sent = Assert.Single(cw.Requests, r => r.Path.EndsWith("/time/entries", StringComparison.Ordinal)).Json();
        Assert.False(sent.TryGetProperty("member", out _));

        AssertEveryRequestCarriesTheTenantsCredential(cw);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ARateLimitedNoteIsSentAgainAfterTheWaitItAskedForAndLandsOnce()
    {
        await using var cw = await StartAsync();
        cw.Inject(IsNotePost, RateLimited("1"));
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle() with { Destinations = ["ticket_note"] });

        Assert.True(Assert.Single(published.Results).Ok);
        var posts = cw.Requests.Where(IsNotePost).ToList();
        Assert.Equal([429, 201], posts.Select(p => p.Status));
        Assert.True(posts[1].At - posts[0].At >= TimeSpan.FromMilliseconds(900), $"Sent again after {(posts[1].At - posts[0].At).TotalMilliseconds} ms, inside the second it asked for.");
        _ = Assert.Single(cw.Notes);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ARateLimitLongerThanTheProviderWaitsIsAnsweredAtOnceAndMayBeSentAgain()
    {
        // Thirty seconds is past MaxRetryWait: the note is not sent again inside ConnectWise's window,
        // the technician hears now, and Retry is offered. The time entry goes on its own.
        await using var cw = await StartAsync();
        cw.Inject(IsNotePost, RateLimited("30"));
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle());

        var note = published.Results.Single(r => r.Destination == "ticket_note");
        Assert.False(note.Ok);
        Assert.Equal("unavailable", note.Kind);
        Assert.True(note.Retryable);
        Assert.Equal("ConnectWise is rate-limiting requests. Try again in 30 seconds.", note.Error);
        Assert.Equal(30, note.RetryAfterSeconds);
        _ = Assert.Single(cw.Requests, IsNotePost);
        Assert.Empty(cw.Notes);
        Assert.True(published.Results.Single(r => r.Destination == "time_entry").Ok);
        _ = Assert.Single(cw.TimeEntries);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ARateLimitOnEveryAttemptStopsAtThree()
    {
        await using var cw = await StartAsync();
        cw.Inject(IsNotePost, RateLimited("0"), times: 5);
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle() with { Destinations = ["ticket_note"] });

        var note = Assert.Single(published.Results);
        Assert.Equal("unavailable", note.Kind);
        Assert.True(note.Retryable);
        Assert.Equal(3, cw.Requests.Count(IsNotePost));
        Assert.Empty(cw.Notes);
    }

    [Fact]
    public async Task AServerErrorAfterTheNoteLeftIsUnknownAndNotSentAgain()
    {
        // ConnectWise may have saved it before failing: sending it again is a second note on the ticket.
        await using var cw = await StartAsync();
        cw.Inject(IsNotePost, new Reply(503, new { code = "ServiceUnavailable", message = "The service is unavailable." }));
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle() with { Destinations = ["ticket_note"] });

        var note = Assert.Single(published.Results);
        Assert.False(note.Ok);
        Assert.Equal("unknown", note.Kind);
        Assert.False(note.Retryable);
        Assert.Equal("ConnectWise did not confirm the request, so it may have been saved. Check ConnectWise before publishing again, so nothing is added twice.", note.Error);
        _ = Assert.Single(cw.Requests, IsNotePost);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task AWrongKeyIsRefusedInWordsTheTechnicianCanActOnAndIsNeverSentAgain()
    {
        await using var cw = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu: null);
        var wrong = $"{cw.CompanyId}+{cw.PublicKey}:{Guid.NewGuid():N}";
        await backend.SignInAsync(connectWiseCredential: wrong);

        var published = await backend.PublishAsync(StandInBackend.Bundle());
        using var check = await backend.Client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var checkedNow = await check.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);

        Assert.All(published.Results, r =>
        {
            Assert.False(r.Ok);
            Assert.Equal("unauthenticated", r.Kind);
            Assert.False(r.Retryable);
            Assert.Equal("ConnectWise rejected the API key. Update it in Settings → Integrations.", r.Error);
        });
        Assert.False(checkedNow!.Ok);
        Assert.Equal("ConnectWise rejected the API key. Update it in Settings → Integrations.", checkedNow.Message);

        // One request per destination and one for the check, each refused, none tried again.
        Assert.Equal(3, cw.Requests.Count);
        Assert.All(cw.Requests, r => Assert.Equal(401, r.Status));
        Assert.All(cw.Requests, r => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(wrong)), r.Header("Authorization")));
        Assert.Empty(cw.Notes);
        Assert.Empty(cw.TimeEntries);
        backend.Remember(wrong);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task TestConnectionWithTheRightKeyAsksSystemInfoAndSaysConnected()
    {
        await using var cw = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        using var check = await backend.Client.PostAsync(new Uri("/v1/integrations/connectwise/check", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var answer = await check.Content.ReadFromJsonAsync<IntegrationCheckResponse>(TestContext.Current.CancellationToken);

        Assert.True(answer!.Ok, answer.Message);
        var info = Assert.Single(cw.Requests);
        Assert.Equal(("GET", $"{ConnectWiseStandIn.ApiRoot}system/info", 200), (info.Method, info.Path, info.Status));
        AssertEveryRequestCarriesTheTenantsCredential(cw);
    }

    [Fact]
    public async Task AMemberWithoutTheTimeEntryRightIsForbiddenAndTheNoteStillLands()
    {
        await using var cw = await StartAsync();
        _ = cw.Denied.Add("time/entries");
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle());

        Assert.True(published.Results.Single(r => r.Destination == "ticket_note").Ok);
        var time = published.Results.Single(r => r.Destination == "time_entry");
        Assert.False(time.Ok);
        Assert.Equal("forbidden", time.Kind);
        Assert.False(time.Retryable);
        Assert.Equal("ConnectWise says this API member may not do that. Ask your ConnectWise administrator for the permission.", time.Error);
        _ = Assert.Single(cw.Requests, r => r.Path.EndsWith("/time/entries", StringComparison.Ordinal));
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task AFieldLevelRefusalReachesTheTechnicianInConnectWisesOwnWords()
    {
        // A board that will not take a time entry without a member, which the provider does not send.
        await using var cw = await StartAsync();
        cw.RequireMemberOnTimeEntries = true;
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle());

        var time = published.Results.Single(r => r.Destination == "time_entry");
        Assert.False(time.Ok);
        Assert.Equal("invalid", time.Kind);
        Assert.False(time.Retryable);
        Assert.Equal("ConnectWise refused the request: The member field is required for a time entry on this board. This is a ScreenTail bug; there is nothing to change on your side.", time.Error);
        _ = Assert.Single(cw.Requests, r => r.Path.EndsWith("/time/entries", StringComparison.Ordinal));
        Assert.Empty(cw.TimeEntries);
        Assert.True(published.Results.Single(r => r.Destination == "ticket_note").Ok);
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ANoteForATicketThatIsNotThereIsNotFound()
    {
        await using var cw = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var published = await backend.PublishAsync(StandInBackend.Bundle(ticketId: "999999") with { Destinations = ["ticket_note"] });

        var note = Assert.Single(published.Results);
        // The kind is the enum name in lower case, as for every provider failure on this wire.
        Assert.Equal("notfound", note.Kind);
        Assert.Equal("ConnectWise has no such ticket. Pick a different ticket.", note.Error);
        Assert.Equal(404, Assert.Single(cw.Requests).Status);
    }

    [Fact]
    public async Task ASearchFindsOpenTicketsNewestFirstOnePageAtATime()
    {
        await using var cw = await StartAsync();
        cw.AddTickets(40, "Printer queue stuck");
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var rows = await SearchAsync(backend, "printer");

        var expected = cw.Tickets.Where(t => !t.Closed && t.Summary.Contains("printer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Entered).Take(25).ToList();
        Assert.Equal(expected.Select(t => t.Id.ToString(CultureInfo.InvariantCulture)), rows.Select(r => r.Id));
        Assert.Equal(expected.Select(t => t.Company), rows.Select(r => r.Company));
        Assert.Equal(expected.Select(t => t.Status), rows.Select(r => r.Status));
        Assert.DoesNotContain(rows, r => r.Id == "731900");

        // One page asked for, with the fields the provider reads; the next page's link is not followed.
        var list = Assert.Single(cw.Requests);
        Assert.Equal("summary contains 'printer' and closedFlag = false", list.Query["conditions"]);
        Assert.Equal("dateEntered desc", list.Query["orderBy"]);
        Assert.Equal(("25", "1"), (list.Query["pageSize"], list.Query["page"]));
        Assert.Equal("id,summary,company/name,status/name", list.Query["fields"]);
        backend.Remember("printer");
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ATicketNumberIsLookedUpFirstAndMatchesFollow()
    {
        await using var cw = await StartAsync();
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var rows = await SearchAsync(backend, Ticket);

        var first = Assert.Single(rows);
        Assert.Equal((Ticket, "Printer offline in reception", "Acme Dental", "In Progress"), (first.Id, first.Summary, first.Company, first.Status));
        Assert.Equal([$"{ConnectWiseStandIn.ApiRoot}service/tickets/{Ticket}", $"{ConnectWiseStandIn.ApiRoot}service/tickets"], cw.Requests.Select(r => r.Path));
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task TheRecentListIsTheTenOpenTicketsLastTouched()
    {
        await using var cw = await StartAsync();
        cw.AddTickets(12, "Mailbox over quota");
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var rows = await SearchAsync(backend, string.Empty);

        var expected = cw.Tickets.Where(t => !t.Closed).OrderByDescending(t => t.LastUpdated).Take(10).Select(t => t.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(expected, rows.Select(r => r.Id));
        var list = Assert.Single(cw.Requests);
        Assert.Equal(("closedFlag = false", "_info/lastUpdated desc", "10"), (list.Query["conditions"], list.Query["orderBy"], list.Query["pageSize"]));
        backend.AssertTheLogHoldsNothingThatWasSent();
    }

    [Fact]
    public async Task ASearchThatMeetsAnOutageIsAskedAgain()
    {
        // A read changes nothing, so an outage is retried; it is the write above that is not.
        await using var cw = await StartAsync();
        cw.Inject(r => r.Method == "GET" && r.Path.EndsWith("/service/tickets", StringComparison.Ordinal), new Reply(502, new { code = "BadGateway", message = "Upstream unavailable." }));
        await using var backend = new StandInBackend(api, cw, hudu: null);
        await backend.SignInAsync();

        var rows = await SearchAsync(backend, "printer");

        Assert.Equal(2, rows.Count);
        Assert.Equal([502, 200], cw.Requests.Select(r => r.Status));
    }

    internal static async Task<ConnectWiseStandIn> StartAsync()
    {
        var cw = new ConnectWiseStandIn();
        await cw.StartAsync();
        return cw;
    }

    private static bool IsNotePost(RecordedRequest r) => r.Method == "POST" && r.Path.EndsWith("/notes", StringComparison.Ordinal);

    private static Reply RateLimited(string retryAfter) =>
        new(429, new { code = "TooManyRequests", message = "Rate limit exceeded." }, new Dictionary<string, string> { ["Retry-After"] = retryAfter });

    private static void AssertEveryRequestCarriesTheTenantsCredential(ConnectWiseStandIn cw)
    {
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(cw.Credential));
        Assert.All(cw.Requests, r =>
        {
            Assert.Equal(basic, r.Header("Authorization"));
            Assert.Equal(cw.ClientId, r.Header("clientId"));
            Assert.Contains("application/json", r.Header("Accept"), StringComparison.Ordinal);
            Assert.InRange(r.Status, 200, 299);
        });
    }

    private static async Task<List<TicketSearchRow>> SearchAsync(StandInBackend backend, string query)
    {
        using var response = await backend.Client.GetAsync(new Uri($"/v1/psa/tickets?q={Uri.EscapeDataString(query)}", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<TicketSearchResponse>(TestContext.Current.CancellationToken);
        return [.. answer!.Tickets];
    }
}
