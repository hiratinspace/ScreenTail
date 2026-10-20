using System.Net;
using Microsoft.AspNetCore.Hosting;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Auth;

/// <summary>
/// The signing key has no default, and the host refuses to serve without a real one (ST-008; 2026-10-12
/// review, T-183).
///
/// <see cref="StartupTests"/> checks the rule on <see cref="TokenIssuer.Validation"/> alone. What it could
/// not see is when the rule runs: the bearer options are built the first time a request is
/// authenticated, so a host with no key started, answered <c>/health</c>, and only failed at the first
/// call with a token. "Refuses to start" has to mean the host, not the first request.
/// </summary>
public sealed class SigningKeyTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public void JwtOptionsHasNoDefaultKey()
    {
        // A default here would be a key in the repository, signing tokens for any tenant.
        Assert.Equal(string.Empty, new JwtOptions().SigningKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sixteen-chars-!!")]
    public void TheHostWillNotServeWithoutASigningKey(string key)
    {
        using var host = api.WithWebHostBuilder(builder => builder.UseSetting("Jwt:SigningKey", key));

        // Building the server is starting the host; nothing has been asked of it yet.
        var thrown = Record.Exception(() => host.Server);

        Assert.NotNull(thrown);
        Assert.Contains("Jwt:SigningKey", Flatten(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostWithAKeyServes()
    {
        // The control: the same host with the fixture's key starts and answers.
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Every message down the inner exceptions, since a host may wrap what Program threw.</summary>
    private static string Flatten(Exception? thrown)
    {
        var messages = new List<string>();
        for (var at = thrown; at is not null; at = at.InnerException)
        {
            messages.Add(at.Message);
        }

        return string.Join(" | ", messages);
    }
}
