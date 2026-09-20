using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// The drafting provider as the running service builds it, rather than as a test does (ST-063).
///
/// Every other test of <see cref="GeminiProvider"/> hands it an <see cref="HttpClient"/> it made itself,
/// and the live measurement does the same. The one place the client is built for real is
/// <c>Program.cs</c>, and nothing looked at what came out of it. What came out was the container's
/// unnamed client: no base address, so the provider's relative URL threw before a byte left the machine,
/// and the default hundred-second timeout in place of the configured one. Drafting could not have worked
/// on any deployment (2026-09-20 review).
///
/// The stub is attached to the named client only. A service wired to any other client never reaches it,
/// which is the failure this exists to catch.
/// </summary>
public sealed class ProviderWiringTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task TheServiceTalksToTheProviderThroughTheClientThatWasConfiguredForIt()
    {
        var provider = new RecordingHandler();
        using var host = api.WithWebHostBuilder(builder =>
        {
            // A key, so the service tries at all. It goes nowhere: the handler below answers instead
            // of the network, and a client that bypasses the handler has no address to send it to.
            _ = builder.UseSetting("Summarization:ApiKey", "not-a-real-key");
            _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
                nameof(GeminiProvider),
                options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = provider)));
        });

        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SummarizationService>();

        var result = await service.DraftAsync(Guid.NewGuid(), Bundle(), TestContext.Current.CancellationToken);

        Assert.Equal(
            new Uri("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent"),
            provider.Asked);
        Assert.Equal(SummarizeStatus.Unavailable, result.Status);
    }

    private static SummarizeBundle Bundle() => new()
    {
        SessionId = "s1",
        DurationMs = 60_000,
        Frames = [new BundleFrame("f1", 1_000, "Services")],
        Transcript = [],
    };

    /// <summary>Answers "busy" to everything, and remembers where it was asked.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
