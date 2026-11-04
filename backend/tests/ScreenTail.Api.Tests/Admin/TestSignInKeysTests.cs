using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// The browser tests' stand-in for Google's keys (Testing:GoogleSigningKeys, ADR-0009 "Testing in a real
/// browser"). The point of these tests is the first one: a host that is not Development does not start
/// with the setting, so the key whose private half the browser tests hold can never sign anyone in to a
/// deployment.
/// </summary>
public sealed class TestSignInKeysTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopmentTheSettingStopsTheServiceFromStarting(string environment)
    {
        using var rsa = RSA.Create(2048);
        using var host = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseEnvironment(environment);
            _ = builder.UseSetting(TestSignInOptions.Setting, PublicSet(rsa, "e2e-key"));
        });

        var thrown = Assert.ThrowsAny<Exception>(() => host.Services);

        Assert.Contains(TestSignInOptions.Setting, thrown.ToString(), StringComparison.Ordinal);
        Assert.Contains("outside Development", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InProductionWithoutTheSettingTheKeysAreGooglesOwn()
    {
        // The control for the refusal above: the same host starts in Production when the setting is absent,
        // and its key source is the one that fetches Google's published set.
        using var host = new KeySourceHost(Environments.Production, testKeys: string.Empty);

        Assert.IsType<GoogleSigningKeys>(host.Services.GetRequiredService<IGoogleSigningKeys>());
    }

    [Fact]
    public void InDevelopmentWithoutTheSettingTheKeysAreGooglesOwn()
    {
        using var host = new KeySourceHost(Environments.Development, testKeys: string.Empty);

        Assert.IsType<GoogleSigningKeys>(host.Services.GetRequiredService<IGoogleSigningKeys>());
    }

    [Fact]
    public void InDevelopmentTheSettingReplacesGooglesKeysWithItsOwn()
    {
        using var rsa = RSA.Create(2048);
        using var host = new KeySourceHost(Environments.Development, PublicSet(rsa, "e2e-key"));

        var keys = Assert.IsType<TestGoogleSigningKeys>(host.Services.GetRequiredService<IGoogleSigningKeys>());
        Assert.Equal("e2e-key", Assert.Single(keys.Keys).KeyId);
    }

    [Fact]
    public async Task ATokenSignedByTheTestKeyIsCheckedLikeGooglesAndAnotherKeyIsRefused()
    {
        using var rsa = RSA.Create(2048);
        using var stranger = RSA.Create(2048);
        using var google = new GoogleTokens();
        var options = new TestSignInOptions { GoogleSigningKeys = PublicSet(rsa, "e2e-key") };
        var verifier = new GoogleIdTokenVerifier(
            new TestGoogleSigningKeys(options.Keys()),
            new AdminOptions { GoogleClientId = GoogleTokens.ClientId },
            TimeProvider.System);

        var good = google.Mint(email: "owner@e2e.example", signWith: new RsaSecurityKey(rsa) { KeyId = "e2e-key" });
        var forged = google.Mint(email: "owner@e2e.example", signWith: new RsaSecurityKey(stranger) { KeyId = "e2e-key" });
        var otherAudience = google.Mint(email: "owner@e2e.example", audience: "someone-else.apps.googleusercontent.com", signWith: new RsaSecurityKey(rsa) { KeyId = "e2e-key" });

        Assert.Equal("owner@e2e.example", (await verifier.VerifyAsync(good, CancellationToken.None))?.Email);
        Assert.Null(await verifier.VerifyAsync(forged, CancellationToken.None));
        Assert.Null(await verifier.VerifyAsync(otherAudience, CancellationToken.None));
    }

    [Fact]
    public void ASetHoldingAPrivateKeyIsRefused()
    {
        using var rsa = RSA.Create(2048);
        var withPrivate = JsonSerializer.Serialize(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: true)) { KeyId = "e2e-key" }) } });

        var thrown = Assert.Throws<InvalidOperationException>(() => new TestSignInOptions { GoogleSigningKeys = withPrivate }.Validate(isDevelopment: true));

        Assert.Contains("private key", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"keys":[]}""")]
    [InlineData("""{"keys":[{"kty":"oct","kid":"k","k":"AAAA"}]}""")]
    [InlineData("""{"keys":[{"kty":"RSA","n":"AQAB","e":"AQAB"}]}""")]
    public void ASetThatIsNotPublicRsaKeysWithIdsIsRefused(string value)
    {
        _ = Assert.Throws<InvalidOperationException>(() => new TestSignInOptions { GoogleSigningKeys = value }.Validate(isDevelopment: true));
    }

    [Fact]
    public void UnsetIsAcceptedEverywhere()
    {
        new TestSignInOptions().Validate(isDevelopment: false);
        new TestSignInOptions { GoogleSigningKeys = "  " }.Validate(isDevelopment: false);
    }

    /// <summary>The public half of <paramref name="rsa"/> as a JSON Web Key Set, the way the browser tests hand it over.</summary>
    internal static string PublicSet(RSA rsa, string keyId)
    {
        var p = rsa.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "RSA",
                    ["kid"] = keyId,
                    ["alg"] = "RS256",
                    ["use"] = "sig",
                    ["n"] = Base64UrlEncoder.Encode(p.Modulus),
                    ["e"] = Base64UrlEncoder.Encode(p.Exponent),
                },
            },
        });
    }

    /// <summary>
    /// The API as Program.cs composes it, with no test replacing the key source (ApiFixture puts
    /// <see cref="NoGoogle"/> in its place, which would hide the registration this class checks). No
    /// request is sent, so the database it names is never opened.
    /// </summary>
    private sealed class KeySourceHost(string environment, string testKeys) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.UseEnvironment(environment);
            _ = builder.UseSetting("Jwt:Issuer", "screentail-tests");
            _ = builder.UseSetting("Jwt:Audience", "screentail-api");
            _ = builder.UseSetting("Jwt:SigningKey", ApiFixture.SigningKey);
            _ = builder.UseSetting("ConnectionStrings:Postgres", "Host=never-opened;Database=screentail");
            _ = builder.UseSetting("Summarization:ApiKey", string.Empty);
            _ = builder.UseSetting("Summarization:FallbackApiKey", string.Empty);
            _ = builder.UseSetting("Summarization:Provider", ScreenTail.Api.Summarize.SummarizationOptions.Gemini);
            _ = builder.UseSetting("Providers:Demo", "false");
            _ = builder.UseSetting("ProviderNetwork:AllowPrivateAddresses", "false");
            _ = builder.UseSetting("Admin:GoogleClientId", GoogleTokens.ClientId);
            _ = builder.UseSetting(TestSignInOptions.Setting, testKeys);
        }
    }
}
