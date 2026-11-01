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
