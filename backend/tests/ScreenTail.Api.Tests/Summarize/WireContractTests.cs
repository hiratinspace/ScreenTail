using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.Llm;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The request a client actually sends, against the endpoint that actually receives it (ST-063).
///
/// The backend does not depend on the Windows client and must not, so what binds the two is JSON and
/// the field names are the contract. A contract that lives in two comments is a contract that drifts,
/// and this one drifted before anything could notice: until 2026-09-22 the client's sender was a stub,
/// so the shape had never been posted anywhere. The client's own bundle carries <c>image</c> as a
/// <b>path</b>, and this endpoint has always read it as base64 bytes — one field name, two readings,
/// and no test on either side could see it.
///
/// <c>shared/contracts/summarize-request.v1.json</c> is the fixture both ends read. The client asserts
/// it writes that shape; this asserts the endpoint accepts it, with a real token, real limits and a
/// real ledger. Only the model is stubbed, because what is under test is the request rather than what a
/// model makes of it.
/// </summary>
public sealed class WireContractTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task TheContractIsWhatThisEndpointAccepts()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        var model = new StubModel();

        using var host = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("Summarization:ApiKey", "not-a-real-key");
            _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
                nameof(GeminiProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = model)));
        });

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new StringContent(Contract("request"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Printer offline", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelIsShownTheFramesTheClientSent()
    {
        // The other half of the contract: a field the endpoint accepts but drops on the floor would
        // pass the test above and produce a note written from nothing.
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        var model = new StubModel();

        using var host = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("Summarization:ApiKey", "not-a-real-key");
            _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
                nameof(GeminiProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = model)));
        });

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new StringContent(Contract("request"), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        _ = response;
        using var asked = JsonDocument.Parse(model.Body!);
        var parts = asked.RootElement.GetProperty("contents")[0].GetProperty("parts").EnumerateArray().ToList();

        // Both pictures reached the model, as inline data rather than as a filename.
        Assert.Equal(2, parts.Count(part => part.TryGetProperty("inline_data", out _)));

        // And what the technician said reached it too, inside the session text.
        var session = parts.Last(part => part.TryGetProperty("text", out _)).GetProperty("text").GetString()!;
        Assert.Contains("set it to automatic", session, StringComparison.Ordinal);
    }

    /// <summary>One value out of the shared contract fixture, by name.</summary>
    private static string Contract(string property)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.summarize-request.v1.json")
            ?? throw new InvalidOperationException("The shared contract fixture is not embedded in this assembly.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(property).GetRawText();
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Contract", Seats = 1, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId}@example.com", DisplayName = "T", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device
            {
                Id = deviceId,
                TenantId = tenantId,
                UserId = userId,
                Name = "LAPTOP",
                TokenHash = DeviceTokens.Hash(DeviceTokens.Create()),
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        return (tenantId, userId, deviceId);
    }

    /// <summary>Answers with a usable note, and keeps what it was asked.</summary>
    private sealed class StubModel : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "candidates": [ { "content": { "parts": [ { "text": {{JsonSerializer.Serialize(Draft())}} } ] } } ],
                      "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 10 }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        /// <summary>A note that cites the frames and segments the fixture actually carries.</summary>
        private static string Draft() => """
            {"problem": "Nothing would print.",
             "steps": [{"text": "Found the Print Spooler service stopped.", "confidence": "high",
                        "frame_refs": ["f-0001"], "transcript_refs": ["t-0001"]}],
             "result": "Printing works again.", "follow_ups": [], "suggested_title": "Printer offline",
             "suggested_time_minutes": 21, "kb_candidate": false, "kb_reason": "Routine.",
             "source": "cloud", "prompt_version": "note_v1"}
            """;
    }
}
