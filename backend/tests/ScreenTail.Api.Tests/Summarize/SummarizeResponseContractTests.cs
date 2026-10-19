using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// Every answer the drafting endpoint gives, against the fixture the client reads (ST-063; 2026-10-12
/// review, T-196 and T-197).
///
/// <c>shared/contracts/summarize-response.v1.json</c> lists each outcome with its HTTP status and its
/// body. This drives the real endpoint into each one, with a real token, real limits and a real ledger,
/// and asserts the status and the shape of the body match the fixture. The client's
/// <c>EachOutcomeIsWhatTheOutboxIsTold</c> reads the same file and asserts what its outbox does with it.
/// A rename on either side fails one of the two.
/// </summary>
public sealed class SummarizeResponseContractTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData("ok")]
    [InlineData("rejected")]
    [InlineData("cost_cap_reached")]
    [InlineData("invalid")]
    [InlineData("not_configured")]
    [InlineData("unavailable")]
    public async Task EachOutcomeReachesTheClientAsItsOwnStatus(string name)
    {
        var expected = Outcome(name);
        var model = name switch
        {
            "invalid" => Model.Answering(Broken()),
            "unavailable" => Model.RateLimited($"{expected["retry_after_seconds"]!.GetValue<int>()}s"),
            _ => Model.Answering(OkDraft()),
        };

        using var host = Host(model, configured: name != "not_configured", capUsd: name == "cost_cap_reached" ? 0m : null);
        using var client = await ClientAsync(host);

        var request = name == "rejected"
            ? Request().Replace("\"f-0001\"", "\"has a space\"", StringComparison.Ordinal)
            : Request();
        using var response = await PostAsync(client, request);

        Assert.Equal(expected["http_status"]!.GetValue<int>(), (int)response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(Shape(expected["body"]!), Shape(body));
        Assert.Equal(expected["body"]!["status"]!.GetValue<string>(), body["status"]!.GetValue<string>());

        if (expected["retry_after_seconds"] is { } seconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds.GetValue<int>()), response.Headers.RetryAfter?.Delta);
        }
        else
        {
            Assert.Null(response.Headers.RetryAfter);
        }
    }

    /// <summary>Google's answer to a key it does not recognise: a 400, not a 401.</summary>
    private const string KeyNotValid = """
        { "error": { "code": 400, "message": "API key not valid. Please pass a valid API key.", "status": "INVALID_ARGUMENT",
          "details": [ { "@type": "type.googleapis.com/google.rpc.ErrorInfo", "reason": "API_KEY_INVALID", "domain": "googleapis.com" } ] } }
        """;

    /// <summary>Google's answer to an account that may not use the model from where it is asking.</summary>
    private const string NotForThisAccount = """
        { "error": { "code": 400, "message": "User location is not supported for the API use.", "status": "FAILED_PRECONDITION" } }
        """;

    [Theory]
    [InlineData(401, "")]
    [InlineData(403, "")]
    [InlineData(404, "")]
    [InlineData(400, KeyNotValid)]
    [InlineData(400, NotForThisAccount)]
    public async Task AProviderThatRefusesThisDeploymentLeavesTheSessionQueued(int status, string refusal)
    {
        // Review round 1 of the 2026-10-12 fixes. The client fails a 422 for good, and a wrong key, an
        // account the model will not serve and a retired model all came back as one. Every session a
        // device finished while an operator's setting was wrong was lost, with nothing to redraft once
        // it was fixed. They are this deployment not being set up to draft, which the client queues.
        var expected = Outcome("not_configured");
        using var host = Host(Model.Refusing((HttpStatusCode)status, refusal));
        using var client = await ClientAsync(host);

        using var response = await PostAsync(client, Request());

        Assert.Equal(expected["http_status"]!.GetValue<int>(), (int)response.StatusCode);
        Assert.Equal("retry", expected["client"]!.GetValue<string>());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(Shape(expected["body"]!), Shape(body));
        Assert.Equal("not_configured", body["status"]!.GetValue<string>());

        // The operator reading the technician's report is pointed at the setting.
        Assert.Contains("Summarization__", body["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "candidates": [ { "content": { "parts": [ { "text": "" } ] } } ], "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 10 } }""")]
    [InlineData("""{ "promptFeedback": { "blockReason": "OTHER" }, "usageMetadata": { "promptTokenCount": 10 } }""")]
    [InlineData("""{ "candidates": [ { "finishReason": "MAX_TOKENS", "content": {} } ], "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 10 } }""")]
    public async Task AModelThatAnsweredWithNothingIsFailedAndSaysSo(string envelope)
    {
        // The model ran, was paid, and said nothing that can be shown. Asking again buys the same answer,
        // so it is the invalid outcome the client fails, and its reason must not tell the technician the
        // session is queued.
        var expected = Outcome("invalid");
        using var host = Host(Model.Raw(envelope));
        using var client = await ClientAsync(host);

        using var response = await PostAsync(client, Request());

        Assert.Equal(expected["http_status"]!.GetValue<int>(), (int)response.StatusCode);
        Assert.Equal("failed", expected["client"]!.GetValue<string>());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(Shape(expected["body"]!), Shape(body));
        var reason = body["reason"]!.GetValue<string>();
        Assert.DoesNotContain("queued", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tried again", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.5s", 1)]
    [InlineData("26.656292589s", 27)]
    [InlineData("86400s", 86400)]
    [InlineData("100000s", 86400)]
    public async Task ARetryAfterIsWholeSecondsRoundedUpAndNeverMoreThanADay(string retryDelay, int seconds)
    {
        // Rounded up, because a retry a fraction of a second early is the request that earned the rate
        // limit. Held to a day, because a quota reopens within one and a longer wait parks the session
        // where nobody looks.
        using var host = Host(Model.RateLimited(retryDelay));
        using var client = await ClientAsync(host);

        using var response = await PostAsync(client, Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task ADeviceAlreadyDraftingAsManyAsItMayIsToldItIsBusyBeforeItsBundleIsRead()
    {
        // 2026-10-12 review (E-35). Each request bound up to 16 MB before anything looked at who was
        // asking, and held it for up to a minute. The third at once from one device is refused first.
        var expected = Outcome("busy");
        var model = Model.Holding();
        using var host = Host(model);
        var (tenant, devices) = await SeedAsync(devices: 1);
        using var client = Client(host, tenant, devices[0]);

        var held = Enumerable.Range(0, DraftingConcurrency.PerDevice).Select(_ => PostAsync(client, Request())).ToList();
        await model.WaitForAsync(DraftingConcurrency.PerDevice);

        // Not JSON at all: refused as busy rather than as malformed, so nothing read the body first.
        using var refused = await PostAsync(client, "{ this is not a bundle");

        Assert.Equal(expected["http_status"]!.GetValue<int>(), (int)refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(Shape(expected["body"]!), Shape(body));
        Assert.Equal("busy", body["status"]!.GetValue<string>());

        model.Release();
        foreach (var response in await Task.WhenAll(held))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        // And the places are given back once those finish.
        using var after = await PostAsync(client, Request());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ATenantAlreadyDraftingAsManyAsItMayIsToldItIsBusy()
    {
        var model = Model.Holding();
        using var host = Host(model);
        var (tenant, devices) = await SeedAsync(devices: DraftingConcurrency.PerTenant + 1);
        var clients = devices.Select(device => Client(host, tenant, device)).ToList();

        var held = clients.Take(DraftingConcurrency.PerTenant).Select(client => PostAsync(client, Request())).ToList();
        await model.WaitForAsync(DraftingConcurrency.PerTenant);

        using var refused = await PostAsync(clients[^1], Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Another tenant is not held up by this one.
        var (other, otherDevices) = await SeedAsync(devices: 1);
        using var otherClient = Client(host, other, otherDevices[0]);
        var elsewhere = PostAsync(otherClient, Request());
        await model.WaitForAsync(DraftingConcurrency.PerTenant + 1);

        model.Release();
        using (var response = await elsewhere)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        foreach (var response in await Task.WhenAll(held))
        {
            response.Dispose();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    /// <summary>The field names and nesting, as a sorted list of paths; values left out.</summary>
    private static List<string> Shape(JsonNode? node, string path = "")
    {
        var paths = new List<string>();
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    paths.AddRange(Shape(value, $"{path}.{key}"));
                }

                break;

            case JsonArray array:
                paths.AddRange(array.Count == 0 ? [$"{path}[]"] : Shape(array[0], $"{path}[]"));
                break;

            default:
                paths.Add(path);
                break;
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static JsonNode Outcome(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.summarize-response.v1.json")
            ?? throw new InvalidOperationException("The shared response contract is not embedded in this assembly.");
        var document = JsonNode.Parse(stream)!;
        return document["outcomes"]!.AsArray().Single(outcome => outcome!["name"]!.GetValue<string>() == name)!;
    }

    private static string Request()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.summarize-request.v1.json")
            ?? throw new InvalidOperationException("The shared request contract is not embedded in this assembly.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("request").GetRawText();
    }

    /// <summary>The ok outcome's draft, as the model writes it.</summary>
    private static string OkDraft() => Outcome("ok")["body"]!["draft"]!.ToJsonString();

    /// <summary>The ok draft citing a frame the session does not have, which no repair fixes.</summary>
    private static string Broken() => OkDraft().Replace("\"f-0001\"", "\"f-9999\"", StringComparison.Ordinal);

    private WebApplicationFactory<Program> Host(Model model, bool configured = true, decimal? capUsd = null) =>
        api.WithWebHostBuilder(builder =>
        {
            if (configured)
            {
                _ = builder.UseSetting("Summarization:ApiKey", "not-a-real-key");
            }

            if (capUsd is { } cap)
            {
                _ = builder.UseSetting("Summarization:DailyCostCapUsd", cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
                nameof(GeminiProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = model)));
        });

    private async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host)
    {
        var (tenant, devices) = await SeedAsync(devices: 1);
        return Client(host, tenant, devices[0]);
    }

    private HttpClient Client(WebApplicationFactory<Program> host, (Guid Tenant, Guid User) tenant, Guid device)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            api.Issuer.ForDevice(tenant.Tenant, tenant.User, device).Token);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string body) =>
        client.PostAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    private async Task<((Guid Tenant, Guid User) Tenant, List<Guid> Devices)> SeedAsync(int devices)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceIds = Enumerable.Range(0, devices).Select(_ => Guid.NewGuid()).ToList();

        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Contract", Seats = devices, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId}@example.com", DisplayName = "T", CreatedAt = DateTimeOffset.UtcNow });
            foreach (var deviceId in deviceIds)
            {
                db.Devices.Add(new Device
                {
                    Id = deviceId,
                    TenantId = tenantId,
                    UserId = userId,
                    Name = "LAPTOP",
                    TokenHash = DeviceTokens.Hash(DeviceTokens.Create()),
                    ActivatedAt = DateTimeOffset.UtcNow,
                });
            }

            return await db.SaveChangesAsync();
        });

        return ((tenantId, userId), deviceIds);
    }

    /// <summary>The drafting model, answering as each case needs.</summary>
    private sealed class Model : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _answer;
        private readonly TaskCompletionSource? _release;
        private int _entered;
        private readonly SemaphoreSlim _arrivals = new(0);

        private Model(Func<HttpResponseMessage> answer, bool holds)
        {
            _answer = answer;
            _release = holds ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        }

        public static Model Answering(string draft) => new(() => Envelope(draft), holds: false);

        /// <summary>Answers with a good draft, but only once <see cref="Release"/> is called.</summary>
        public static Model Holding() => new(() => Envelope(OkDraft()), holds: true);

        /// <summary>The provider refusing with this status and body, before any model ran.</summary>
        public static Model Refusing(HttpStatusCode status, string body) => new(
            () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            holds: false);

        /// <summary>A 200 with this envelope, exactly as written.</summary>
        public static Model Raw(string envelope) => new(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") },
            holds: false);

        public static Model RateLimited(string retryDelay) => new(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    $$"""
                    { "error": { "code": 429, "status": "RESOURCE_EXHAUSTED", "details": [
                        { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "{{retryDelay}}" } ] } }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            },
            holds: false);

        public void Release() => _release?.TrySetResult();

        /// <summary>
        /// Until this many calls have reached the model, all of them still waiting. Bounded, so a gate that
        /// lets nothing through fails the test rather than hanging the run.
        /// </summary>
        public async Task WaitForAsync(int calls)
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            patience.CancelAfter(TimeSpan.FromSeconds(30));
            while (Volatile.Read(ref _entered) < calls)
            {
                await _arrivals.WaitAsync(patience.Token);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _entered);
            _ = _arrivals.Release();
            if (_release is not null)
            {
                await _release.Task.WaitAsync(ct);
            }

            return _answer();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _arrivals.Dispose();
            }

            base.Dispose(disposing);
        }

        private static HttpResponseMessage Envelope(string draft) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "candidates": [ { "content": { "parts": [ { "text": {{JsonSerializer.Serialize(draft)}} } ] } } ],
                  "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 10 }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
    }
}
