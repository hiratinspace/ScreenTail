using System.Net;
using System.Text;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// Every answer of the API says it is not to be rendered, framed, sniffed or followed by a referrer
/// (ST-099 hardening, ADR-0009): the admin's routes, the devices' routes, refusals, a body refused
/// before it is read, and a path that does not exist.
/// </summary>
public sealed class SecurityHeadersTests(AdminApiFixture api) : IClassFixture<AdminApiFixture>
{
    public static TheoryData<string, string> Answers => new()
    {
        { "GET", "/health" },
        { "GET", "/v1/admin/session" },
        { "POST", "/v1/admin/session" },
        { "POST", "/v1/admin/session/nonce" },
        { "POST", "/v1/admin/session/end" },
        { "POST", "/v1/devices/activate" },
        { "GET", "/v1/me" },
        { "GET", "/v1/admin/technicians" },
        { "GET", "/no/such/path" },
    };

    private static void AssertHeaders(HttpResponseMessage response)
    {
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
        Assert.Contains("camera=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task EveryAnswerCarriesTheHeaders(string method, string path)
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        AssertHeaders(response);
    }

    [Fact]
    public async Task ABodyRefusedBeforeItIsReadCarriesThemToo()
    {
        using var client = api.CreateClient(new() { HandleCookies = false });
        using var content = new ByteArrayContent(new byte[(int)ScreenTail.Api.Auth.AdminSession.AnonymousBodyLimit + 1]);
        content.Headers.ContentType = new("application/json");

        using var response = await client.PostAsync(new Uri("/v1/admin/session", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertHeaders(response);
    }

    [Fact]
    public void TheApiPolicyAllowsNoSourceAtAll()
    {
        Assert.StartsWith("default-src 'none';", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);
    }
}
