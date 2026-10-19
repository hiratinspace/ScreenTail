using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.ConnectWise;

namespace ScreenTail.Api.Tests.Providers.ConnectWise;

/// <summary>
/// ConnectWise Manage's REST API, as the provider speaks it (ST-091, ST-092): what goes on the wire and
/// what each answer becomes. The API is recorded shapes in <see cref="ScriptedConnectWise"/>; nothing
/// here reaches a real one.
/// </summary>
public sealed class ConnectWiseProviderTests : IDisposable
{
    private const string Secret = "acme+PUBLICKEY:PRIVATEKEY";
    private readonly ScriptedConnectWise _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task EveryRequestCarriesBasicAuthAndTheClientId()
    {
        // The Basic string is the whole credential the vault holds, companyId+publicKey:privateKey, and
        // the clientId is the deployment's vendor registration. Without either ConnectWise answers 401.
        var provider = Provider();

        _ = await provider.CheckAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(_api.Requests);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), request.Headers.Authorization.Parameter);
        Assert.Equal("11111111-2222-3333-4444-555555555555", Assert.Single(request.Headers.GetValues("clientId")));
        Assert.EndsWith("/v4_6_release/apis/3.0/system/info", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAClientIdNothingIsSentAndTheOperatorIsTold()
    {
        var provider = Provider(clientId: string.Empty);

        var result = await provider.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_api.Requests);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Contains("ConnectWise:ClientId", result.Error.Todo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeywordSearchAsksForOpenTicketsNewestFirstTwentyFiveAtATime()
    {
        // ST-092: "printer" → open tickets newest-first, limit 25 with pagination.
        var result = await Provider().SearchTicketsAsync("printer", TestContext.Current.CancellationToken);

        var request = Assert.Single(_api.Requests);
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.Contains("summary contains 'printer'", query, StringComparison.Ordinal);
        Assert.Contains("closedFlag = false", query, StringComparison.Ordinal);
        Assert.Contains("orderBy=dateEntered desc", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=25", query, StringComparison.Ordinal);
        Assert.Contains("page=1", query, StringComparison.Ordinal);
        Assert.True(result.Ok);
        var tickets = result.Value!;
        Assert.Equal(["48213", "48190"], tickets.Select(t => t.Id));
        Assert.Equal("Acme Dental", tickets[0].Company);
        Assert.Equal("In Progress", tickets[0].Status);
    }

    [Fact]
    public async Task RecentTicketsAreOpenOnesLastTouchedFirstTenAtATime()
    {
        // ST-092's default, for the picker on focus: no condition on the summary, the tenant's open
        // tickets in the order they were last updated, and a short page — this is a glance, not a search.
        var result = await Provider().RecentTicketsAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(_api.Requests);
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.EndsWith("/service/tickets", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain("summary contains", query, StringComparison.Ordinal);
        Assert.Contains("closedFlag = false", query, StringComparison.Ordinal);
        Assert.Contains("orderBy=_info/lastUpdated desc", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=10", query, StringComparison.Ordinal);
        Assert.True(result.Ok);
        Assert.Equal(["48213", "48190"], result.Value!.Select(t => t.Id));
    }

    [Fact]
    public async Task ANumberIsLookedUpAsAnIdFirst()
    {
        // ST-092: numeric → exact id first. A technician who typed 48213 is not asking for tickets whose
        // summary mentions it; the exact one leads, and keyword matches follow.
        var result = await Provider().SearchTicketsAsync("48213", TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("48213", result.Value![0].Id);
        Assert.EndsWith("/service/tickets/48213", _api.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANumberThatIsNoTicketIsAnEmptyListNotAnError()
    {
        var result = await Provider().SearchTicketsAsync("99999", TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task QuotesInAQueryCannotEscapeTheCondition()
    {
        // The conditions string is ConnectWise's own little language. A quote in what the technician
        // typed must not end the string early.
        _ = await Provider().SearchTicketsAsync("o'brien's printer", TestContext.Current.CancellationToken);

        var query = Uri.UnescapeDataString(Assert.Single(_api.Requests).RequestUri!.Query);
        Assert.Contains("summary contains 'obriens printer'", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANoteIsPostedWithTheRightFlagsAndComesBackWithItsId()
    {
        var provider = Provider();

        var internalNote = await provider.AddNoteAsync(new TicketNote("48213", "Problem, steps, result.", Internal: true, []), TestContext.Current.CancellationToken);
        var discussion = await provider.AddNoteAsync(new TicketNote("48213", "Problem, steps, result.", Internal: false, []), TestContext.Current.CancellationToken);

        Assert.True(internalNote.Ok);
        Assert.Equal("90001", internalNote.Value!.Id);
        var first = JsonDocument.Parse(_api.Bodies[0]!).RootElement;
        var second = JsonDocument.Parse(_api.Bodies[1]!).RootElement;
        Assert.Equal("Problem, steps, result.", first.GetProperty("text").GetString());
        Assert.True(first.GetProperty("internalAnalysisFlag").GetBoolean());
        Assert.False(first.GetProperty("detailDescriptionFlag").GetBoolean());
        Assert.False(second.GetProperty("internalAnalysisFlag").GetBoolean());
        Assert.True(second.GetProperty("detailDescriptionFlag").GetBoolean());
        Assert.True(discussion.Ok);
    }

    [Fact]
    public async Task AttachmentsFollowTheNoteAsDocumentsOnTheTicket()
    {
        // ST-093 AC2 from the provider's side: three frames become three documents on the ticket, after
        // the note, each named so a technician can tell them apart.
        var note = new TicketNote("48213", "Body", Internal: true,
        [
            new NoteAttachment("frame-1.jpg", "image/jpeg", new byte[] { 1 }),
            new NoteAttachment("frame-2.jpg", "image/jpeg", new byte[] { 2 }),
            new NoteAttachment("frame-3.jpg", "image/jpeg", new byte[] { 3 }),
        ]);

        var result = await Provider().AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(4, _api.Requests.Count);
        Assert.All(_api.Requests.Skip(1), r => Assert.EndsWith("/system/documents", r.RequestUri!.AbsolutePath, StringComparison.Ordinal));
        Assert.Contains("recordType", _api.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("48213", _api.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("frame-1.jpg", _api.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAttachmentRefusedAfterTheNoteLandedNamesTheNote()
    {
        // The note is on the ticket. Reporting it as failed is what made Retry post it a second time,
        // with every screenshot again (2026-10-12 review, T-211): it is a success that says what is missing.
        _api.FailingRoute = ("/system/documents", HttpStatusCode.BadRequest);
        var note = new TicketNote("48213", "Body", Internal: true,
        [
            new NoteAttachment("screenshot-1.jpg", "image/jpeg", new byte[] { 1 }),
            new NoteAttachment("screenshot-2.jpg", "image/jpeg", new byte[] { 2 }),
        ]);

        var result = await Provider().AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("90001", result.Value!.Id);
        var missing = Assert.IsType<ProviderError>(result.Value.Incomplete);
        Assert.Contains("90001", missing.What, StringComparison.Ordinal);
        Assert.Contains("2 of 2 screenshots", missing.What, StringComparison.Ordinal);
        Assert.Contains("The script said no", missing.What, StringComparison.Ordinal);
        Assert.EndsWith(".", missing.Todo, StringComparison.Ordinal);
        Assert.Single(_api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/notes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneRefusedAttachmentDoesNotStopTheOthers()
    {
        // The second of three is refused; the first and third still go, and the note says which one is missing.
        using var refusedOnce = new RefuseOne(_api, "screenshot-2.jpg");
        var note = new TicketNote("48213", "Body", Internal: true,
        [
            new NoteAttachment("screenshot-1.jpg", "image/jpeg", new byte[] { 1 }),
            new NoteAttachment("screenshot-2.jpg", "image/jpeg", new byte[] { 2 }),
            new NoteAttachment("screenshot-3.jpg", "image/jpeg", new byte[] { 3 }),
        ]);

        var result = await Provider(refusedOnce).AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Contains("1 of 3 screenshots", result.Value!.Incomplete!.What, StringComparison.Ordinal);
        Assert.Contains("screenshot-2.jpg", result.Value.Incomplete.What, StringComparison.Ordinal);
        Assert.DoesNotContain("screenshot-1.jpg", result.Value.Incomplete.What, StringComparison.Ordinal);
        var reached = _api.Bodies.Where((_, i) => _api.Requests[i].RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, reached.Count);
        Assert.Contains(reached, b => b!.Contains("screenshot-1.jpg", StringComparison.Ordinal));
        Assert.Contains(reached, b => b!.Contains("screenshot-3.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachmentsGoUpAFewAtATimeAndNeverMore()
    {
        // In parallel so twenty-five screenshots fit inside the publish deadline, and bounded so one
        // publish is not a burst the PSA's rate limit answers.
        using var counting = new CountingHandler(_api);
        var note = new TicketNote("48213", "Body", Internal: true,
            [.. Enumerable.Range(1, 12).Select(i => new NoteAttachment($"screenshot-{i}.jpg", "image/jpeg", new byte[] { (byte)i }))]);

        var result = await Provider(counting).AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Null(result.Value!.Incomplete);
        Assert.InRange(counting.MostAtOnce, 2, ConnectWiseProvider.AttachmentsAtOnce);
        Assert.Equal(12, _api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ADeadlineDuringTheAttachmentsStillReportsTheNote()
    {
        // The publish ran out of time while the screenshots were going up. The note had already landed,
        // so it is reported with its id rather than lost in a cancellation. The four that were on their
        // way may have attached, so the advice is to look before adding them; the two that never left
        // did not, and are named as missing.
        // The deadline comes the moment the fourth is on its way, so which ones had left is not a race.
        _api.HangingRoute = "/system/documents";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var fourth = new DeadlineWhenOnTheirWay(_api, ConnectWiseProvider.AttachmentsAtOnce, deadline);
        var note = new TicketNote("48213", "Body", Internal: true,
            [.. Enumerable.Range(1, 6).Select(i => new NoteAttachment($"screenshot-{i}.jpg", "image/jpeg", new byte[] { (byte)i }))]);

        var result = await Provider(fourth).AddNoteAsync(note, deadline.Token);

        Assert.True(result.Ok);
        Assert.Equal("90001", result.Value!.Id);
        var incomplete = result.Value.Incomplete!;
        Assert.Contains("2 of 6 screenshots did not attach (screenshot-5.jpg, screenshot-6.jpg)", incomplete.What, StringComparison.Ordinal);
        Assert.Contains("4 may not have (screenshot-1.jpg, screenshot-2.jpg, screenshot-3.jpg, screenshot-4.jpg)", incomplete.What, StringComparison.Ordinal);
        Assert.Contains("Look for screenshot-1.jpg, screenshot-2.jpg, screenshot-3.jpg, screenshot-4.jpg before adding them", incomplete.Todo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[1, 2")]
    public async Task AnUploadWhoseAnswerCannotBeReadMayHaveAttached(string body)
    {
        // ConnectWise took the screenshot and answered with something that is not JSON. The note is
        // still reported with its id, the other screenshot still goes, and the unreadable one is "may
        // not have attached", because it may well be on the ticket.
        _api.UnreadableRoute = ("/system/documents", body);
        var note = new TicketNote("48213", "Body", Internal: true, [new NoteAttachment("screenshot-1.jpg", "image/jpeg", new byte[] { 1 })]);

        var result = await Provider().AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("90001", result.Value!.Id);
        var incomplete = result.Value.Incomplete!;
        Assert.Contains("1 of 1 screenshots may not have attached (screenshot-1.jpg)", incomplete.What, StringComparison.Ordinal);
        Assert.DoesNotContain(body, incomplete.What, StringComparison.Ordinal);
        Assert.Contains("Look for screenshot-1.jpg before adding it", incomplete.Todo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachmentsOnAnInternalNoteAreMarkedPrivate()
    {
        // S-163: without the flag, whether the screenshots show in the customer portal was ConnectWise's
        // default rather than the note type the technician chose.
        var attachment = new NoteAttachment("screenshot-1.jpg", "image/jpeg", new byte[] { 1 });

        _ = await Provider().AddNoteAsync(new TicketNote("48213", "Body", Internal: true, [attachment]), TestContext.Current.CancellationToken);
        _ = await Provider().AddNoteAsync(new TicketNote("48213", "Body", Internal: false, [attachment]), TestContext.Current.CancellationToken);

        var documents = _api.Requests.Select((r, i) => (r, i)).Where(x => x.r.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal)).Select(x => _api.Bodies[x.i]!).ToList();
        Assert.Equal(2, documents.Count);
        Assert.Equal("true", FormField(documents[0], "privateFlag"));
        Assert.Equal("false", FormField(documents[1], "privateFlag"));
    }

    [Fact]
    public async Task ATimeEntryForAnInternalNoteKeepsItsTextInternal()
    {
        // S-83: the description of a time entry can reach the customer (an invoice, the portal), so an
        // internal note's text goes in the internal notes, and nothing is copied or emailed anywhere.
        var start = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero);

        _ = await Provider().AddTimeEntryAsync(new TimeEntry("48213", start, 30, "Internal words.", Billable: true, Internal: true), TestContext.Current.CancellationToken);
        _ = await Provider().AddTimeEntryAsync(new TimeEntry("48213", start, 30, "Customer words.", Billable: true, Internal: false), TestContext.Current.CancellationToken);

        var inside = JsonDocument.Parse(_api.Bodies[0]!).RootElement;
        var outside = JsonDocument.Parse(_api.Bodies[1]!).RootElement;
        Assert.Equal("Internal words.", inside.GetProperty("internalNotes").GetString());
        Assert.False(inside.TryGetProperty("notes", out _));
        Assert.Equal("Customer words.", outside.GetProperty("notes").GetString());
        Assert.False(outside.TryGetProperty("internalNotes", out _));
        foreach (var body in new[] { inside, outside })
        {
            foreach (var flag in new[] { "addToDetailDescriptionFlag", "addToInternalAnalysisFlag", "addToResolutionFlag", "emailResourceFlag", "emailContactFlag", "emailCcFlag" })
            {
                Assert.False(body.GetProperty(flag).GetBoolean(), flag);
            }
        }
    }

    [Fact]
    public async Task ATimeEntryIsMinutesOnTheTicketAsAWindowWithTheBillingChoice()
    {
        // ST-094 AC1: 30 min → a 0.5 h entry with the note as description.
        var start = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero);

        var result = await Provider().AddTimeEntryAsync(new TimeEntry("48213", start, 30, "Fixed the spooler.", Billable: true, Internal: false), TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal("70001", result.Value!.Id);
        var body = JsonDocument.Parse(Assert.Single(_api.Bodies)!).RootElement;
        Assert.Equal(48213, body.GetProperty("chargeToId").GetInt32());
        Assert.Equal("ServiceTicket", body.GetProperty("chargeToType").GetString());
        Assert.Equal("2026-09-24T14:02:00Z", body.GetProperty("timeStart").GetString());
        Assert.Equal("2026-09-24T14:32:00Z", body.GetProperty("timeEnd").GetString());
        Assert.Equal("Fixed the spooler.", body.GetProperty("notes").GetString());
        Assert.Equal("Billable", body.GetProperty("billableOption").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.Unauthenticated)]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, ProviderErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, ProviderErrorKind.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderErrorKind.Unavailable)]
    public async Task EachAnswerBecomesTheKindWithTheRightNextStep(HttpStatusCode status, ProviderErrorKind kind)
    {
        _api.Failing = status;

        var result = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(kind, result.Error!.Kind);
        Assert.EndsWith(".", result.Error.What, StringComparison.Ordinal);
        Assert.EndsWith(".", result.Error.Todo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalCarriesConnectWisesOwnWordsAndNothingElse()
    {
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { code = "InvalidObject", message = "text is required", errors = new[] { new { code = "Required", message = "Text is required.", field = "text" } } }),
        });

        var result = await Provider().AddNoteAsync(new TicketNote("48213", "", Internal: true, []), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Contains("Text is required", result.Error.What, StringComparison.Ordinal);
        Assert.DoesNotContain("http", result.Error.What, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARateLimitIsRetriedWithBackoffAndThenGivenUpOnWithTheWaitItAsked()
    {
        // ST-091 AC2: 429 → exponential backoff with jitter, max 3 attempts.
        _api.Scripted.Enqueue(TooMany(retryAfterSeconds: 2));
        _api.Scripted.Enqueue(TooMany(retryAfterSeconds: 2));
        _api.Scripted.Enqueue(TooMany(retryAfterSeconds: 7));

        var time = new RecordedWaits();

        var result = await Provider(time: time).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _api.Requests.Count);
        Assert.Equal(ProviderErrorKind.Unavailable, result.Error!.Kind);
        Assert.True(result.Error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(7), result.Error.RetryAfter);

        // The wait ConnectWise asked for is the wait it gets (2026-10-12 review): a retry inside the
        // window is another 429 against a limit the tenant's whole team shares.
        Assert.Equal(2, time.Waits.Count);
        Assert.All(time.Waits, wait => Assert.True(wait >= TimeSpan.FromSeconds(2), $"Waited {wait}."));
    }

    [Fact]
    public async Task AThirdAttemptThatSucceedsIsASuccess()
    {
        _api.Scripted.Enqueue(TooMany(retryAfterSeconds: 1));
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Provider(time: new RecordedWaits()).CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(3, _api.Requests.Count);
    }

    [Fact]
    public async Task AWrongKeyIsNotRetried()
    {
        // Retrying a wrong API key is how an integration becomes an account lockout.
        _api.Failing = HttpStatusCode.Unauthorized;

        _ = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Single(_api.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task ARedirectSaysTheSiteNeedsCheckingNotThatScreenTailHasABug(HttpStatusCode status)
    {
        // Redirects are not followed (security check 2026-10-14), so a moved site comes back as a 3xx.
        // That is the stored site's problem, and the technician is pointed at it.
        _api.Failing = status;

        var result = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Single(_api.Requests);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Equal("The ConnectWise site answered with a redirect, and ScreenTail does not follow redirects.", result.Error.What);
        Assert.Equal("Check the site address in Settings → Integrations.", result.Error.Todo);
    }

    [Fact]
    public async Task ATimeEntryOfNoMinutesNeverReachesTheWire()
    {
        var result = await Provider().AddTimeEntryAsync(new TimeEntry("48213", DateTimeOffset.UtcNow, 0, "n", true), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Empty(_api.Requests);
    }

    [Theory]
    [InlineData("/notes")]
    [InlineData("/time/entries")]
    public async Task AWriteConnectWiseDidNotConfirmIsSentOnceAndSaysToCheckFirst(string route)
    {
        // 2026-10-12 review: a 5xx after the body left may be a note that was saved. Sending it again is
        // the duplicate ST-094 forbids, so it is sent once and the technician is told to look first.
        _api.FailingRoute = (route, HttpStatusCode.BadGateway);
        var provider = Provider();

        var result = route == "/notes"
            ? await provider.AddNoteAsync(new TicketNote("48213", "Body", Internal: true, []), TestContext.Current.CancellationToken)
            : await provider.AddTimeEntryAsync(new TimeEntry("48213", DateTimeOffset.UtcNow, 30, "Body", Billable: true), TestContext.Current.CancellationToken);

        Assert.Single(_api.Requests);
        Assert.Equal(ProviderErrorKind.Unknown, result.Error!.Kind);
        Assert.False(result.Error.Retryable);
        Assert.Equal("ConnectWise did not confirm the request, so it may have been saved.", result.Error.What);
        Assert.Equal("Check ConnectWise before publishing again, so nothing is added twice.", result.Error.Todo);
    }

    [Theory]
    [InlineData("""{"ticketId":48213}""", "application/json")]
    [InlineData("[1,2,3]", "application/json")]
    [InlineData("<html>Saved</html>", "text/html")]
    public async Task ANoteAcceptedWithAnAnswerThatHasNoIdIsNotCalledPublishedAndIsNotAnException(string body, string mediaType)
    {
        // The contract says a published note comes back with its id; without one, a retry cannot tell
        // "already there" from "not there". It is reported as perhaps saved, never thrown.
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

        var result = await Provider().AddNoteAsync(new TicketNote("48213", "Body", Internal: true, []), TestContext.Current.CancellationToken);

        Assert.Single(_api.Requests);
        Assert.Equal(ProviderErrorKind.Unknown, result.Error!.Kind);
        Assert.False(result.Error.Retryable);
    }

    [Fact]
    public async Task AnAttachmentThatWasNotConfirmedNamesTheNoteThatWasPublished()
    {
        // A 504 after the upload left is the provider's Unknown: sent once, not again, and the note that
        // landed is reported with its id. The screenshot may be on the ticket, so it is one to look for
        // before adding it by hand, not one that is simply missing.
        _api.FailingRoute = ("/system/documents", HttpStatusCode.GatewayTimeout);
        var note = new TicketNote("48213", "Body", Internal: true, [new NoteAttachment("frame-1.jpg", "image/jpeg", new byte[] { 1 })]);

        var result = await Provider().AddNoteAsync(note, TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Requests.Count);
        Assert.True(result.Ok);
        Assert.Equal("90001", result.Value!.Id);
        var incomplete = result.Value.Incomplete!;
        Assert.Equal(ProviderErrorKind.Unknown, incomplete.Kind);
        Assert.StartsWith("The note was published as 90001, but 1 of 1 screenshots may not have attached (frame-1.jpg): ", incomplete.What, StringComparison.Ordinal);
        Assert.Contains("Look for frame-1.jpg before adding it", incomplete.Todo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAttachmentThatWasRefusedIsMissingAndNotOneToLookFor()
    {
        // A 4xx is a refusal: the screenshot is not there, so it is named as missing and nothing says to
        // look for it first.
        _api.FailingRoute = ("/system/documents", HttpStatusCode.BadRequest);
        var note = new TicketNote("48213", "Body", Internal: true, [new NoteAttachment("frame-1.jpg", "image/jpeg", new byte[] { 1 })]);

        var result = await Provider().AddNoteAsync(note, TestContext.Current.CancellationToken);

        var incomplete = result.Value!.Incomplete!;
        Assert.NotEqual(ProviderErrorKind.Unknown, incomplete.Kind);
        Assert.StartsWith("The note was published as 90001, but 1 of 1 screenshots did not attach (frame-1.jpg): ", incomplete.What, StringComparison.Ordinal);
        Assert.DoesNotContain("Look for", incomplete.Todo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASearchAnsweredInTheWrongShapeIsATypedErrorAndNotAnException()
    {
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { tickets = Array.Empty<object>() }) });

        var result = await Provider().SearchTicketsAsync("printer", TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Equal("The ConnectWise site answered with something that is not its API.", result.Error.What);
    }

    private ConnectWiseProvider Provider(string clientId = "11111111-2222-3333-4444-555555555555", TimeProvider? time = null) => Provider(_api, clientId, time);

    private static ConnectWiseProvider Provider(HttpMessageHandler handler, string clientId = "11111111-2222-3333-4444-555555555555", TimeProvider? time = null) =>
        new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://na.myconnectwise.net/v4_6_release/apis/3.0/") },
            Secret,
            new ConnectWiseOptions { ClientId = clientId, BackoffBase = TimeSpan.Zero },
            time);

    /// <summary>The value of one field of a multipart form, as the scripted API recorded it.</summary>
    private static string? FormField(string body, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(body, $"name=\"?{name}\"?\r\n(?:[^\r\n]+\r\n)*\r\n([^\r\n]*)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Refuses the upload of one named file, once; everything else goes to the scripted API. The file is
    /// found in the body, because the provider wraps each request's content to know when it left.
    /// </summary>
    private sealed class RefuseOne(HttpMessageHandler inner, string fileName) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal)
                && request.Content is not null
                && (await request.Content.ReadAsStringAsync(cancellationToken)).Contains($"filename={fileName}", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { code = "Forced", message = "That file was refused" }) };
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Counts how many uploads are in flight at once, holding each a moment so they can overlap.</summary>
    /// <summary>Ends the publish's deadline as the given number of screenshots are on their way at once.</summary>
    private sealed class DeadlineWhenOnTheirWay(HttpMessageHandler inner, int count, CancellationTokenSource deadline) : DelegatingHandler(inner)
    {
        private int _sent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal) && Interlocked.Increment(ref _sent) == count)
            {
                await deadline.CancelAsync();
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _now;
        private int _most;

        public int MostAtOnce => Volatile.Read(ref _most);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/system/documents", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var now = Interlocked.Increment(ref _now);
            int most;
            while (now > (most = Volatile.Read(ref _most)) && Interlocked.CompareExchange(ref _most, now, most) != most)
            {
            }

            try
            {
                await Task.Delay(30, cancellationToken);
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _now);
            }
        }
    }

    private static HttpResponseMessage TooMany(int retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }
}

/// <summary>The contract every PSA provider meets (ST-090), run against the recorded ConnectWise.</summary>
public sealed class ConnectWiseMeetsTheContract : PsaProviderContract, IDisposable
{
    private readonly ScriptedConnectWise _api = new();
    private readonly ConnectWiseProvider _provider;

    public void Dispose() => _api.Dispose();

    public ConnectWiseMeetsTheContract()
    {
        _provider = new ConnectWiseProvider(
            new HttpClient(_api) { BaseAddress = new Uri("https://na.myconnectwise.net/v4_6_release/apis/3.0/") },
            "acme+PUBLICKEY:PRIVATEKEY",
            new ConnectWiseOptions { ClientId = "11111111-2222-3333-4444-555555555555", BackoffBase = TimeSpan.Zero });
    }

    protected override IPsaProvider Provider => _provider;

    protected override string KnownTicketId => "48213";

    protected override void Break(ProviderError? error) => _api.Failing = error is null ? null : ScriptedConnectWise.StatusFor(error.Kind);
}
