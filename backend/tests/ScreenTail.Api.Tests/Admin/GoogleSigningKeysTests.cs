using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// The cache in front of Google's published keys (ST-099): how long a copy is kept, how often an unknown
/// key id may cause a fetch, the deadline on a fetch, and what happens when Google does not answer. A
/// scripted handler stands where Google would, so nothing here leaves the machine.
/// </summary>
public sealed class GoogleSigningKeysTests : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly HeldClock _clock = new();
    private readonly Scripted _google = new();
    private readonly GoogleSigningKeys _keys;

    public GoogleSigningKeysTests()
    {
        _keys = new GoogleSigningKeys(() => new HttpClient(_google, disposeHandler: false), _clock);
    }

    public void Dispose()
    {
        _keys.Dispose();
        _google.Dispose();
        _rsa.Dispose();
    }

    [Fact]
    public async Task TheKeySetIsFetchedFromGooglesAddressAndKeptForItsMaxAge()
    {
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(2));

        var first = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(119));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal("k1", Assert.Single(first).KeyId);
        Assert.Equal(1, _keys.Fetches);
        Assert.Equal(GoogleSigningKeys.Address, _google.LastAsked);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        Assert.Equal(2, _keys.Fetches);
    }

    [Fact]
    public async Task AMaxAgeOutsideTheBoundsIsHeldToThem()
    {
        // A day at most, whatever Google says: a key it withdraws stops being trusted within a day.
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromDays(30));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _clock.Advance(GoogleSigningKeys.LongestCache);
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        Assert.Equal(2, _keys.Fetches);

        // Five minutes at least, so a zero max-age cannot make every sign-in a request to Google.
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.Zero);
        _clock.Advance(GoogleSigningKeys.LongestCache);
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _clock.Advance(GoogleSigningKeys.ShortestCache - TimeSpan.FromSeconds(1));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        Assert.Equal(3, _keys.Fetches);
    }

    [Fact]
    public async Task AnUnknownKeyIdFetchesAtMostOnceEveryFiveMinutes()
    {
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(6));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);

        // A stream of made-up key ids is a stream of refresh asks. Only the first in five minutes fetches.
        _clock.Advance(GoogleSigningKeys.RefreshFloor);
        for (var i = 0; i < 20; i++)
        {
            _ = await _keys.GetAsync(refresh: true, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, _keys.Fetches);
    }

    [Fact]
    public async Task AFailedFetchKeepsACopyThatIsStillFresh()
    {
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(1));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _google.Fail(HttpStatusCode.ServiceUnavailable);
        _clock.Advance(GoogleSigningKeys.RefreshFloor);

        var kept = await _keys.GetAsync(refresh: true, TestContext.Current.CancellationToken);

        Assert.Equal("k1", Assert.Single(kept).KeyId);
    }

    [Fact]
    public async Task RefreshAsksWhileGoogleFailsMakeOneRequestPerFloor()
    {
        // The failure path counts toward the floor too. Without that, every made-up key id sent while
        // Google is failing would be another request to Google.
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(6));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _google.Fail(HttpStatusCode.ServiceUnavailable);
        _clock.Advance(GoogleSigningKeys.RefreshFloor);

        for (var i = 0; i < 20; i++)
        {
            var kept = await _keys.GetAsync(refresh: true, TestContext.Current.CancellationToken);
            Assert.Equal("k1", Assert.Single(kept).KeyId);
            _clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(2, _keys.Fetches);
    }

    [Fact]
    public async Task WithNoCopyAtAllAFailureIsNotRetriedForThirtySeconds()
    {
        _google.Fail(HttpStatusCode.ServiceUnavailable);
        for (var i = 0; i < 20; i++)
        {
            _ = await Assert.ThrowsAsync<GoogleKeysUnavailableException>(() => _keys.GetAsync(refresh: i % 2 == 0, TestContext.Current.CancellationToken));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, _keys.Fetches);

        _clock.Advance(GoogleSigningKeys.RetryAfterFailure);
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(1));
        Assert.Equal("k1", Assert.Single(await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken)).KeyId);
        Assert.Equal(2, _keys.Fetches);
    }

    [Fact]
    public async Task AFreshCopyAnswersWhileAnotherCallersFetchIsUnderWay()
    {
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(6));
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _clock.Advance(GoogleSigningKeys.RefreshFloor);
        _google.Hang();
        _google.ResetAsked();

        var stuck = _keys.GetAsync(refresh: true, TestContext.Current.CancellationToken);
        await _google.Asked.WaitAsync(TestContext.Current.CancellationToken);
        var plain = _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        var another = _keys.GetAsync(refresh: true, TestContext.Current.CancellationToken);

        Assert.True(plain.IsCompletedSuccessfully);
        Assert.True(another.IsCompletedSuccessfully);
        Assert.Equal("k1", Assert.Single(await another).KeyId);
        Assert.False(stuck.IsCompleted);
        Assert.Equal(2, _keys.Fetches);

        _clock.Advance(GoogleSigningKeys.FetchTimeout);
        Assert.Equal("k1", Assert.Single(await stuck).KeyId);
    }

    [Fact]
    public async Task WithNoFreshCopyAFailedFetchFailsClosed()
    {
        _google.Fail(HttpStatusCode.InternalServerError);
        _ = await Assert.ThrowsAsync<GoogleKeysUnavailableException>(() => _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken));

        // A copy that has gone stale is not used either: Google may have withdrawn one of its keys.
        _google.Answer(KeySet("k1"), maxAge: TimeSpan.FromHours(1));
        _clock.Advance(GoogleSigningKeys.RetryAfterFailure);
        _ = await _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        _google.Fail(HttpStatusCode.InternalServerError);
        _clock.Advance(TimeSpan.FromHours(1));
        _ = await Assert.ThrowsAsync<GoogleKeysUnavailableException>(() => _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"keys":[]}""")]
    [InlineData("""{"keys":[{"kty":"oct","kid":"k1","k":"AAAA"}]}""")]
    [InlineData("not json")]
    public async Task AnAnswerWithNoRsaKeyIsNotAKeySet(string body)
    {
        _google.Answer(body, maxAge: TimeSpan.FromHours(1));

        _ = await Assert.ThrowsAsync<GoogleKeysUnavailableException>(() => _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerLargerThanAnyKeySetIsRefused()
    {
        _google.Answer(new string(' ', 70 * 1024) + KeySet("k1"), maxAge: TimeSpan.FromHours(1));

        _ = await Assert.ThrowsAsync<GoogleKeysUnavailableException>(() => _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFetchThatDoesNotFinishInTenSecondsIsGivenUp()
    {
        _google.Hang();
        var fetching = _keys.GetAsync(refresh: false, TestContext.Current.CancellationToken);
        await _google.Asked.WaitAsync(TestContext.Current.CancellationToken);

        _clock.Advance(GoogleSigningKeys.FetchTimeout);

        _ = await Assert.ThrowsAsync<GoogleKeysUnavailableException>(() => fetching);
    }

    private string KeySet(string kid)
    {
        var p = _rsa.ExportParameters(includePrivateParameters: false);
        return $$"""{"keys":[{"kty":"RSA","alg":"RS256","use":"sig","kid":"{{kid}}","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}","e":"{{Base64UrlEncoder.Encode(p.Exponent)}}"}]}""";
    }

    /// <summary>Google, as the test says it answers.</summary>
    private sealed class Scripted : HttpMessageHandler
    {
        private Func<CancellationToken, Task<HttpResponseMessage>> _reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        private TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri? LastAsked { get; private set; }

        public void ResetAsked() => _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Asked => _asked.Task;

        public void Answer(string body, TimeSpan maxAge) => _reply = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = maxAge };
            return Task.FromResult(response);
        };

        public void Fail(HttpStatusCode status) => _reply = _ => Task.FromResult(new HttpResponseMessage(status));

        public void Hang() => _reply = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAsked = request.RequestUri;
            _ = _asked.TrySetResult();
            return _reply(cancellationToken);
        }
    }
}
