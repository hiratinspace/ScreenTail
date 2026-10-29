using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// What every PSA and documentation call does with an outage, a rate limit and an answer it cannot use
/// (2026-10-12 review). ConnectWise and Hudu both send through <see cref="ProviderHttp"/>, so these are
/// the rules for both.
///
/// A write is a note, a time entry, a document or an article. Sending one again after the provider may
/// already have saved it is a duplicate on a customer's ticket, so a write is tried again only when
/// nothing of it left this machine, or when the provider said in so many words that it did not take it
/// (a rate limit). A read is tried again as before.
/// </summary>
public sealed class ProviderHttpTests : IDisposable
{
    private readonly Steps _site = new();
    private readonly RecordedWaits _time = new();

    public void Dispose() => _site.Dispose();

    [Fact]
    public async Task AWriteWhoseBodyLeftAndThenTimedOutIsNotSentAgain()
    {
        _site.Then(async (request, ct) =>
        {
            _ = await request.Content!.ReadAsStringAsync(ct);
            throw new TaskCanceledException("The deadline passed.");
        });

        var result = await SendAsync(HttpMethod.Post);

        Assert.Single(_site.Requests);
        AssertMayHaveLanded(result);
    }

    [Fact]
    public async Task AWriteWhoseConnectionDroppedAfterItsBodyLeftIsNotSentAgain()
    {
        _site.Then(async (request, ct) =>
        {
            _ = await request.Content!.ReadAsStringAsync(ct);
            throw new HttpRequestException("The connection was reset.");
        });

        var result = await SendAsync(HttpMethod.Post);

        Assert.Single(_site.Requests);
        AssertMayHaveLanded(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task AWriteAnsweredWithAServerErrorIsNotSentAgain(HttpStatusCode status)
    {
        // A gateway that gave up is not a provider that did nothing: the note behind it may be saved.
        _site.Then(Answer(status));

        var result = await SendAsync(HttpMethod.Post);

        Assert.Single(_site.Requests);
        AssertMayHaveLanded(result);
    }

    [Fact]
    public async Task AWriteAnsweredSeeOtherWasDoneAndIsNotCalledARedirectProblem()
    {
        // 303 is how a site says "done, look over there" after a write, not that the site moved.
        _site.Then(Answer(HttpStatusCode.SeeOther));

        var result = await SendAsync(HttpMethod.Post);

        Assert.Single(_site.Requests);
        AssertMayHaveLanded(result);
    }

    [Fact]
    public async Task AWriteWhoseConnectionNeverOpenedIsTriedAgain()
    {
        // Nothing of the body was read, so nothing was sent and nothing can have been saved.
        _site.Then((_, _) => throw new HttpRequestException("No connection could be made."));
        _site.Then(Answer(HttpStatusCode.OK, """{"id":1}"""));

        var result = await SendAsync(HttpMethod.Post);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.Equal(2, _site.Requests.Count);
    }

    [Fact]
    public async Task ARateLimitedWriteIsTriedAgain()
    {
        // A 429 is the provider saying it did not take the request, so sending it again is safe.
        _site.Then(Answer(HttpStatusCode.TooManyRequests));
        _site.Then(Answer(HttpStatusCode.OK, """{"id":1}"""));

        var result = await SendAsync(HttpMethod.Post);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.Equal(2, _site.Requests.Count);
    }

    [Fact]
    public async Task AReadThatTimedOutOrFailedIsStillTriedAgain()
    {
        _site.Then((_, _) => throw new TaskCanceledException("The deadline passed."));
        _site.Then(Answer(HttpStatusCode.ServiceUnavailable));
        _site.Then(Answer(HttpStatusCode.OK, "{}"));

        var result = await SendAsync(HttpMethod.Get);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.Equal(3, _site.Requests.Count);
    }

    [Fact]
    public async Task ACallerWhoHangsUpIsNotAnsweredWithAnError()
    {
        // Unchanged: the caller's own cancellation is not an outage and is not retried or reported.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _site.Then((_, ct) => throw new TaskCanceledException("Cancelled.", null, ct));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendAsync(HttpMethod.Post, ct: cancelled.Token));
    }

    [Fact]
    public async Task ARateLimitWaitsAsLongAsTheProviderAsked()
    {
        _site.Then(Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3)));
        _site.Then(Answer(HttpStatusCode.OK, "{}"));

        var result = await SendAsync(HttpMethod.Get, new RetryPolicy(TimeSpan.FromMilliseconds(100)) { MaxWait = TimeSpan.FromSeconds(5) });

        Assert.True(result.Ok, result.Error?.ToString());
        var wait = Assert.Single(_time.Waits);
        Assert.True(wait >= TimeSpan.FromSeconds(3), $"Waited {wait} where the provider asked for 3 s.");
    }

    [Fact]
    public async Task ABackoffLongerThanTheWaitAskedForIsKept()
    {
        _site.Then(Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(1)));
        _site.Then(Answer(HttpStatusCode.OK, "{}"));

        var result = await SendAsync(HttpMethod.Get, new RetryPolicy(TimeSpan.FromSeconds(2)) { MaxWait = TimeSpan.FromSeconds(10) });

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.True(Assert.Single(_time.Waits) >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AWaitLongerThanTheCallerCanAffordIsNotSpentAndTheWaitIsReturned()
    {
        // Two more calls inside the window the provider just named are two more 429s against a limit the
        // tenant's whole team shares. Better to say at once when to come back.
        _site.Then(Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(120)));
        _site.Then(Answer(HttpStatusCode.OK, "{}"));

        var result = await SendAsync(HttpMethod.Get, new RetryPolicy(TimeSpan.Zero) { MaxWait = TimeSpan.FromSeconds(5) });

        Assert.Single(_site.Requests);
        Assert.Empty(_time.Waits);
        Assert.Equal(ProviderErrorKind.Unavailable, result.Error!.Kind);
        Assert.True(result.Error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(120), result.Error.RetryAfter);
    }

    [Theory]
    [InlineData(null, "Try again in a minute.")]
    [InlineData(0.0, "Try again in a minute.")]
    [InlineData(-30.0, "Try again in a minute.")]
    [InlineData(0.4, "Try again in 1 second.")]
    [InlineData(45.0, "Try again in 45 seconds.")]
    [InlineData(59.5, "Try again in a minute.")]
    [InlineData(60.0, "Try again in a minute.")]
    [InlineData(61.0, "Try again in 2 minutes.")]
    [InlineData(180.0, "Try again in 3 minutes.")]
    [InlineData(5940.0, "Try again in 99 minutes.")]
    [InlineData(7200.0, "Try again in 2 hours.")]
    [InlineData(7201.0, "Try again in 3 hours.")]
    [InlineData(259200.0, "Try again in 24 hours.")]
    public void TheAdviceIsTheWaitTheProviderNamedRoundedUp(double? seconds, string advice)
    {
        // A technician told "a minute" by a provider that asked for ten tries nine more times into the
        // same limit. The wait is rounded up, never down: sooner than asked is another 429.
        Assert.Equal(advice, ProviderHttp.TryAgain(seconds is { } s ? TimeSpan.FromSeconds(s) : null));
    }

    [Fact]
    public async Task ARetryAfterDateAlreadyPastIsNoReasonToWait()
    {
        _site.Then(Answer(HttpStatusCode.TooManyRequests, retryAt: _time.GetUtcNow() - TimeSpan.FromSeconds(30)));
        _site.Then(Answer(HttpStatusCode.OK, "{}"));

        var result = await SendAsync(HttpMethod.Get, new RetryPolicy(TimeSpan.Zero));

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.All(_time.Waits, wait => Assert.Equal(TimeSpan.Zero, wait));
    }

    [Fact]
    public async Task TheLastAttemptIsNotFollowedByAWait()
    {
        for (var i = 0; i < 3; i++)
        {
            _site.Then(Answer(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(1)));
        }

        var result = await SendAsync(HttpMethod.Get, new RetryPolicy(TimeSpan.Zero));

        Assert.Equal(3, _site.Requests.Count);
        Assert.Equal(2, _time.Waits.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Error!.RetryAfter);
    }

    [Fact]
    public async Task AReadAnsweredWithSomethingThatIsNotJsonIsATypedErrorAndNotAnException()
    {
        // A site address that lands on a sign-in page answers 200 with HTML.
        _site.Then(Answer(HttpStatusCode.OK, "<html><body>Sign in</body></html>", "text/html"));

        var result = await SendAsync(HttpMethod.Get);

        Assert.Single(_site.Requests);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.False(result.Error.Retryable);
        Assert.Equal("The Site site answered with something that is not its API.", result.Error.What);
        Assert.Equal("Check the site address in Settings → Integrations.", result.Error.Todo);
        Assert.DoesNotContain("Sign in", result.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteAcceptedWithAnAnswerThatIsNotJsonMayHaveLanded()
    {
        _site.Then(Answer(HttpStatusCode.Created, "Created", "text/plain"));

        var result = await SendAsync(HttpMethod.Post);

        Assert.Single(_site.Requests);
        Assert.Equal(ProviderErrorKind.Unknown, result.Error!.Kind);
        Assert.False(result.Error.Retryable);
        Assert.Contains("could not be read", result.Error.What, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySuccessIsStillAnEmptyObject()
    {
        _site.Then(Answer(HttpStatusCode.NoContent));

        var result = await SendAsync(HttpMethod.Post);

        Assert.True(result.Ok);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, result.Value.ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAnswerLargerThanTheClientTakesIsRefusedOnceAndNotRetried(bool sized)
    {
        // A tenant chooses its site, and the backend is shared: an answer with no end is one tenant's
        // way to fill every tenant's server. The client stops buffering at its limit, and the site is
        // the problem, so the call is not repeated.
        _site.Then((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Big(4096, sized) }));
        _site.Then((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Big(4096, sized) }));
        using var client = Client(maxBytes: 1024);

        var result = await SendAsync(HttpMethod.Get, client: client);

        Assert.Single(_site.Requests);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Equal("The Site site sent an answer larger than ScreenTail accepts.", result.Error.What);
    }

    [Fact]
    public async Task AWriteWhoseAnswerIsTooLargeMayHaveLanded()
    {
        _site.Then(async (request, ct) =>
        {
            _ = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Big(4096, sized: true) };
        });
        using var client = Client(maxBytes: 1024);

        var result = await SendAsync(HttpMethod.Post, client: client);

        Assert.Single(_site.Requests);
        AssertMayHaveLanded(result);
    }

    [Fact]
    public void TheLimitOnAnAnswerIsMegabytesNotGigabytes()
    {
        Assert.InRange(ProviderHttp.MaxResponseBytes, 1024 * 1024, 16 * 1024 * 1024);
    }

    private static void AssertMayHaveLanded(ProviderResult<System.Text.Json.JsonElement> result)
    {
        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.Unknown, result.Error!.Kind);
        Assert.False(result.Error.Retryable);
        Assert.EndsWith(".", result.Error.What, StringComparison.Ordinal);
        Assert.EndsWith(".", result.Error.Todo, StringComparison.Ordinal);
        Assert.Contains("before", result.Error.Todo, StringComparison.Ordinal);
    }

    private async Task<ProviderResult<System.Text.Json.JsonElement>> SendAsync(HttpMethod method, RetryPolicy? policy = null, HttpClient? client = null, CancellationToken? ct = null)
    {
        using var own = client is null ? Client() : null;
        return await ProviderHttp.SendAsync(
            client ?? own!,
            () => method == HttpMethod.Get
                ? new HttpRequestMessage(method, "things")
                : new HttpRequestMessage(method, "things") { Content = JsonContent.Create(new { text = "A note for the ticket." }) },
            policy ?? new RetryPolicy(TimeSpan.Zero),
            Failure,
            "Site",
            _time,
            ct ?? TestContext.Current.CancellationToken);
    }

    private HttpClient Client(long? maxBytes = null)
    {
        var client = new HttpClient(_site, disposeHandler: false) { BaseAddress = new Uri("https://site.example/api/") };
        if (maxBytes is { } max)
        {
            client.MaxResponseContentBufferSize = max;
        }

        return client;
    }

    private ProviderError Failure(HttpStatusCode status, string body, RetryConditionHeaderValue? retryAfter) => status switch
    {
        HttpStatusCode.TooManyRequests => new(ProviderErrorKind.Unavailable, "Site is rate-limiting requests.", "Try again in a minute.", ProviderHttp.RetryAfter(retryAfter, _time)),
        >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.Unavailable, "Site did not answer.", "Try again in a minute."),
        _ => new(ProviderErrorKind.Invalid, "Site refused the request.", "This is a ScreenTail bug."),
    };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(
        HttpStatusCode status,
        string? body = null,
        string mediaType = "application/json",
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAt = null) => async (request, ct) =>
    {
        if (request.Content is not null)
        {
            _ = await request.Content.ReadAsStringAsync(ct);
        }

        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }
        else if (retryAt is { } date)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
        }

        return response;
    };

    private static HttpContent Big(int bytes, bool sized) => sized
        ? new ByteArrayContent(new byte[bytes])
        : new Unsized(bytes);

    /// <summary>An answer that does not say how long it is, so the limit has to be found by reading.</summary>
    private sealed class Unsized(int bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[bytes]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A site that answers each request with the next step, in order, and remembers what it was asked.</summary>
    private sealed class Steps : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) => _steps.Enqueue(step);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _steps.Count > 0
                ? _steps.Dequeue()(request, cancellationToken)
                : throw new InvalidOperationException("The site was asked more times than the test expected.");
        }
    }
}
