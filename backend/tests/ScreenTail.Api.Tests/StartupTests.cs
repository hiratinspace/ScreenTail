using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Summarize;

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

    [Theory]
    [InlineData("a-real-key\n")]
    [InlineData("  a-real-key  ")]
    [InlineData("a-real-key\r\n")]
    public void AKeyMountedFromAFileStillWorks(string mounted)
    {
        // 2026-09-19 review. A secret mounted from a file or a secrets store almost always arrives with a
        // trailing newline, and an HTTP header value cannot contain one — so building the request threw a
        // FormatException that no catch covered, and every draft came back as a 500 on a deployment whose
        // key was perfectly correct. The operator would have had no way to tell that from a bad key.
        var options = new SummarizationOptions { ApiKey = mounted };

        Assert.Equal("a-real-key", options.ApiKey);
        Assert.True(options.Configured);
    }

    [Fact]
    public void AKeyOfNothingButWhitespaceIsNoKey()
    {
        Assert.False(new SummarizationOptions { ApiKey = "   \n" }.Configured);
    }

    [Fact]
    public void TheApiDocumentIsNotPublishedOutsideDevelopment()
    {
        // It lists every endpoint, every field of the bundle and every error shape: a map of the service
        // for anyone who asks. It was served unauthenticated in every environment, production included.
        var source = File.ReadAllText(ProgramPath());
        var at = source.IndexOf("MapOpenApi", StringComparison.Ordinal);

        Assert.True(at > 0, "MapOpenApi has moved; this test needs to follow it.");
        Assert.Contains("IsDevelopment", source[Math.Max(0, at - 400)..at], StringComparison.Ordinal);
    }

    [Fact]
    public void RequestTimeoutsAreAppliedAndNotOnlyRegistered()
    {
        // AddRequestTimeouts on its own does nothing: the middleware is what applies it. Without
        // UseRequestTimeouts this was a registration with no effect, and the comment above it described
        // authorization instead.
        var source = File.ReadAllText(ProgramPath());

        Assert.Contains("AddRequestTimeouts", source, StringComparison.Ordinal);
        Assert.Contains("UseRequestTimeouts", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Program.cs, found by walking up from the test binary.
    ///
    /// Read as text because what is being checked is a decision in the composition root, and there is no
    /// object to ask: a middleware that was never added leaves nothing behind to inspect. Brittle in the
    /// one way that is acceptable — it fails loudly if the line moves, and says so.
    /// </summary>
    private static string ProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src", "ScreenTail.Api", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Program.cs is not above the test binary.");
    }
}
