using System.Net;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using ScreenTail.Api.Logging;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Logging;

/// <summary>
/// The Anthropic key and the session never reach the log (INV-10), at the most talkative level, through
/// the client the running service builds (ST-063).
///
/// The SDK writes the key into a header of every request, and the HTTP client factory logs requests. The
/// key here is built at runtime in the shape Anthropic issues, so the scrubber's own key patterns are
/// tested too, and no key-shaped literal is committed to a public repository.
/// </summary>
public sealed class ClaudeKeyLoggingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Ocr = "PRIV-OCR-7731 Spooler";

    private const string Speech = "PRIV-SPEECH-4410 clearing the queue";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData(HttpStatusCode.OK, null)]
    public async Task TheKeyAndTheSessionAreNeverLogged(HttpStatusCode status, string? error)
    {
        var key = string.Concat("sk-", "ant-", "api03-", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        using var sink = new StringWriter();
        HttpResponseMessage Reply() => status == HttpStatusCode.OK
            ? new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """
                    { "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
                      "content": [ { "type": "text", "text": "{}" } ], "stop_reason": "end_turn", "stop_sequence": null,
                      "usage": { "input_tokens": 10, "output_tokens": 10 } }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }
            : new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    $$"""{ "type": "error", "error": { "type": "{{error}}", "message": "invalid x-api-key" } }""",
                    Encoding.UTF8,
                    "application/json"),
            };
        var answers = new Answers(Reply);

        using var host = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("Summarization:Provider", "claude");
            _ = builder.UseSetting("Summarization:ApiKey", key);
            _ = builder.UseSetting("Logging:LogLevel:Default", nameof(LogLevel.Trace));
            _ = builder.ConfigureTestServices(services =>
            {
                _ = services.RemoveAll<ILoggerProvider>();
                _ = services.AddSingleton<ILoggerProvider>(new ScrubbingLoggerProvider(sink, LogLevel.Trace));
                _ = services.Configure<HttpClientFactoryOptions>(
                    nameof(ClaudeProvider),
                    options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = answers));
            });
        });

        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SummarizationService>();
        var bundle = new SummarizeBundle
        {
            SessionId = "s1",
            DurationMs = 60_000,
            Frames = [new BundleFrame("f1", 1_000, Ocr)],
            Transcript = [new BundleSegment("t1", 1_200, Speech)],
        };

        var result = await service.DraftAsync(Guid.NewGuid(), bundle, TestContext.Current.CancellationToken);
        var log = Read(sink);

        // Not vacuous: the call was made with the key, and the client logged it.
        Assert.True(answers.SawKey(key), "The request did not carry the configured key.");
        Assert.Contains("HttpClient.ClaudeProvider.LogicalHandler: Start processing HTTP request POST", log, StringComparison.Ordinal);

        Assert.DoesNotContain(key, log, StringComparison.Ordinal);
        Assert.DoesNotContain(key[..20], log, StringComparison.Ordinal);
        Assert.DoesNotContain(key[^16..], log, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIV-OCR-7731", log, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIV-SPEECH-4410", log, StringComparison.Ordinal);
        Assert.DoesNotContain(key, result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    private static string Read(StringWriter sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }

    /// <summary>Answers every call with a fresh reply, and keeps the header values it was sent.</summary>
    private sealed class Answers(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        private readonly List<string> _headers = [];

        public bool SawKey(string key)
        {
            lock (_headers)
            {
                return _headers.Exists(value => value.Contains(key, StringComparison.Ordinal));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_headers)
            {
                _headers.AddRange(request.Headers.SelectMany(header => header.Value));
            }

            return Task.FromResult(reply());
        }
    }
}
