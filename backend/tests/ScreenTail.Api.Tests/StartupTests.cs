using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests;

/// <summary>
/// What the service refuses to start with (ST-008).
///
/// Both checks exist because their absence fails silently. A missing signing key with a development
/// default signs tokens perfectly well and lets anyone who has read the source mint one for any tenant;
/// a short key does the same and looks configured. Neither shows up in a smoke test, so both are refused
/// where they can still be noticed.
/// </summary>
public sealed class StartupTests
{
    [Fact]
    public void AMissingSigningKeyIsRefused()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => TokenIssuer.Validation(new JwtOptions { SigningKey = string.Empty }));

        Assert.Contains("SigningKey", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortSigningKeyIsRefused()
    {
        // Sixteen characters signs HS256 tokens without complaint and is guessable.
        Assert.Throws<InvalidOperationException>(
            () => TokenIssuer.Validation(new JwtOptions { SigningKey = "sixteen-chars-!!" }));
    }

    [Fact]
    public void OnlyHmacSha256IsAccepted()
    {
        // A token that names its own algorithm is a token that can name "none", and every classic JWT
        // break comes from trusting that header.
        var validation = TokenIssuer.Validation(new JwtOptions { SigningKey = ApiFixture.SigningKey });

        Assert.Equal([SecurityAlgorithms.HmacSha256], validation.ValidAlgorithms);
        Assert.True(validation.ValidateIssuer);
        Assert.True(validation.ValidateAudience);
        Assert.True(validation.ValidateLifetime);
        Assert.True(validation.ValidateIssuerSigningKey);
        Assert.Equal(TimeSpan.Zero, validation.ClockSkew);
    }

    [Fact]
    public void ADeviceTokenIsRandomAndStoredOnlyAsAHash()
    {
        var first = DeviceTokens.Create();
        var second = DeviceTokens.Create();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(first, DeviceTokens.Hash(first), StringComparison.Ordinal);
        Assert.Equal(64, DeviceTokens.Hash(first).Length);
        Assert.True(DeviceTokens.Matches(DeviceTokens.Hash(first), DeviceTokens.Hash(first)));
        Assert.False(DeviceTokens.Matches(DeviceTokens.Hash(first), DeviceTokens.Hash(second)));
    }
}
