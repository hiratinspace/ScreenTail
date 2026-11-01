using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// Each check on a Google ID token, one at a time (ST-099, ADR-0009). Every refusal here starts from
/// the token the first test accepts and changes one thing, so a check that stopped being made shows up
/// as exactly one failing test.
/// </summary>
public sealed class GoogleIdTokenVerifierTests : IDisposable
{
    private readonly GoogleTokens _google = new();
    private readonly FakeGoogleKeys _keys;
    private readonly GoogleIdTokenVerifier _verifier;

    public GoogleIdTokenVerifierTests()
    {
        _keys = new FakeGoogleKeys(_google.Key);
        _verifier = Verifier(GoogleTokens.ClientId);
    }

    public void Dispose() => _google.Dispose();

    [Fact]
    public async Task AGoodTokenGivesTheAccountItsVerifiedAddressAndItsName()
    {
        var identity = await _verifier.VerifyAsync(_google.Mint(subject: "1093", email: "Pat.Owner@Acme.example"), TestContext.Current.CancellationToken);

        Assert.NotNull(identity);
        Assert.Equal("1093", identity.Subject);
        Assert.Equal("pat.owner@acme.example", identity.Email);
        Assert.Equal("Pat Owner", identity.Name);
    }

    [Theory]
    [InlineData("accounts.google.com")]
    [InlineData("https://accounts.google.com")]
    public async Task BothOfGooglesIssuerSpellingsAreAccepted(string issuer) =>
        Assert.NotNull(await _verifier.VerifyAsync(_google.Mint(issuer: issuer), TestContext.Current.CancellationToken));

    [Fact]
    public async Task ATokenForAnotherClientIdIsRefused()
    {
        // Another site's Google sign-in. Its token is perfectly good, for that site.
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(audience: "999-somebody-else.apps.googleusercontent.com"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://accounts.google.com.evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("accounts.google.co")]
    public async Task ATokenFromAnotherIssuerIsRefused(string issuer) =>
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(issuer: issuer), TestContext.Current.CancellationToken));

    [Fact]
    public async Task AnExpiredTokenIsRefusedWithNoGrace()
    {
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(expires: DateTimeOffset.UtcNow.AddSeconds(-5)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATokenNotYetValidIsRefused()
    {
        // Google's tokens carry no nbf, but one that names a time still to come is not good yet.
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(notBefore: DateTimeOffset.UtcNow.AddMinutes(5)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAddressIsLoweredInAsciiOnlySoALookalikeStaysItself()
    {
        var identity = await _verifier.VerifyAsync(_google.Mint(email: "KELVIN@Acme.example"), TestContext.Current.CancellationToken);

        Assert.Equal("Kelvin@acme.example", identity!.Email);
    }

    [Fact]
    public async Task AGoodTokenIsCheckedAgainstAKeySetParsedFromGooglesJsonShape()
    {
        // The real key source, fed the JSON body Google serves (only the key is this suite's), and the
        // verifier on top of it: the parsing and the checking meet, with no fake in between.
        var p = _google.Key.Rsa.ExportParameters(includePrivateParameters: false);
        var body = $$"""
            {"keys":[
              {"kty":"RSA","alg":"RS256","use":"sig","kid":"other-key","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}","e":"AQAB"},
              {"e":"{{Base64UrlEncoder.Encode(p.Exponent)}}","kty":"RSA","use":"sig","alg":"RS256","kid":"{{GoogleTokens.KeyId}}","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}"}
            ]}
            """;
        using var handler = new JwksHandler(body);
        using var source = new GoogleSigningKeys(() => new HttpClient(handler, disposeHandler: false), TimeProvider.System);
        var verifier = new GoogleIdTokenVerifier(source, new AdminOptions { GoogleClientId = GoogleTokens.ClientId }, TimeProvider.System);

        Assert.NotNull(await verifier.VerifyAsync(_google.Mint(), TestContext.Current.CancellationToken));
        Assert.Null(await verifier.VerifyAsync(_google.Mint(signWith: _google.Other(GoogleTokens.KeyId)), TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Fetches);
    }

    private sealed class JwksHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                Headers = { CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromHours(6) } },
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData("false")]
    [InlineData("yes")]
    public async Task AnAddressGoogleHasNotVerifiedIsRefused(object verified) =>
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(emailVerified: verified), TestContext.Current.CancellationToken));

    [Fact]
    public async Task AnAddressWithNoVerificationClaimIsRefused() =>
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(omitEmailVerified: true), TestContext.Current.CancellationToken));

    [Fact]
    public async Task TheOlderStringSpellingOfVerifiedIsAccepted() =>
        Assert.NotNull(await _verifier.VerifyAsync(_google.Mint(emailVerified: "true"), TestContext.Current.CancellationToken));

    [Fact]
    public async Task ATokenSignedByAKeyGoogleDidNotPublishIsRefused()
    {
        // The same key id as Google's, a different key: what a forger would send.
        Assert.Null(await _verifier.VerifyAsync(_google.Mint(signWith: _google.Other(GoogleTokens.KeyId)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATamperedPayloadIsRefused()
    {
        var parts = _google.Mint().Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace("owner@acme.example", "owner@birch.example", StringComparison.Ordinal);
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        Assert.Null(await _verifier.VerifyAsync(tampered, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnsignedTokenIsRefused() =>
        Assert.Null(await _verifier.VerifyAsync(GoogleTokens.Unsigned(), TestContext.Current.CancellationToken));

    [Fact]
    public async Task ATokenSignedWithASymmetricKeyIsRefused() =>
        Assert.Null(await _verifier.VerifyAsync(_google.Hmac(), TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not.a.token")]
    public async Task NoTokenOrRubbishIsRefused(string? token) =>
        Assert.Null(await _verifier.VerifyAsync(token, TestContext.Current.CancellationToken));

    [Fact]
    public async Task WithNoClientIdConfiguredNothingIsAccepted()
    {
        // The feature is off. A token for any audience, this suite's included, is refused before a key
        // is asked for.
        var off = Verifier(string.Empty);
        _keys.Unavailable = true;

        Assert.Null(await off.VerifyAsync(_google.Mint(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AKeyGoogleHasJustRotatedInIsFetchedOnce()
    {
        var rotated = _google.Other("rotated-key");
        _keys.AfterRefresh = [_google.Key, rotated];

        var identity = await _verifier.VerifyAsync(_google.Mint(signWith: rotated), TestContext.Current.CancellationToken);

        Assert.NotNull(identity);
        Assert.Equal(1, _keys.Refreshes);
    }

    [Fact]
    public async Task AKnownKeyNeverAsksForARefresh()
    {
        _ = await _verifier.VerifyAsync(_google.Mint(), TestContext.Current.CancellationToken);
        _ = await _verifier.VerifyAsync(_google.Mint(audience: "999-somebody-else.apps.googleusercontent.com"), TestContext.Current.CancellationToken);

        Assert.Equal(0, _keys.Refreshes);
    }

    [Fact]
    public void NoRuleIsSwitchedOff()
    {
        var rules = _verifier.Parameters([_google.Key]);

        Assert.True(rules.ValidateIssuer);
        Assert.True(rules.ValidateAudience);
        Assert.True(rules.ValidateLifetime);
        Assert.True(rules.RequireExpirationTime);
        Assert.True(rules.ValidateIssuerSigningKey);
        Assert.True(rules.RequireSignedTokens);
        Assert.False(rules.TryAllIssuerSigningKeys);
        Assert.Equal([SecurityAlgorithms.RsaSha256], rules.ValidAlgorithms);
        Assert.Equal(GoogleTokens.ClientId, rules.ValidAudience);
        Assert.Equal(TimeSpan.Zero, rules.ClockSkew);
    }

    private GoogleIdTokenVerifier Verifier(string clientId) =>
        new(_keys, new AdminOptions { GoogleClientId = clientId }, TimeProvider.System);
}
