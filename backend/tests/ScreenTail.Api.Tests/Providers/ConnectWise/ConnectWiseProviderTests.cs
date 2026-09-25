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
    public async Task ATimeEntryIsMinutesOnTheTicketAsAWindowWithTheBillingChoice()
    {
        // ST-094 AC1: 30 min → a 0.5 h entry with the note as description.
        var start = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero);

        var result = await Provider().AddTimeEntryAsync(new TimeEntry("48213", start, 30, "Fixed the spooler.", Billable: true), TestContext.Current.CancellationToken);

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

        var result = await Provider().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _api.Requests.Count);
        Assert.Equal(ProviderErrorKind.Unavailable, result.Error!.Kind);
        Assert.True(result.Error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(7), result.Error.RetryAfter);
    }

    [Fact]
    public async Task AThirdAttemptThatSucceedsIsASuccess()
    {
        _api.Scripted.Enqueue(TooMany(retryAfterSeconds: 1));
        _api.Scripted.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Provider().CheckAsync(TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task ATimeEntryOfNoMinutesNeverReachesTheWire()
    {
        var result = await Provider().AddTimeEntryAsync(new TimeEntry("48213", DateTimeOffset.UtcNow, 0, "n", true), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Empty(_api.Requests);
    }

    private ConnectWiseProvider Provider(string clientId = "11111111-2222-3333-4444-555555555555") =>
        new(
            new HttpClient(_api) { BaseAddress = new Uri("https://na.myconnectwise.net/v4_6_release/apis/3.0/") },
            Secret,
            new ConnectWiseOptions { ClientId = clientId, BackoffBase = TimeSpan.Zero });

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
