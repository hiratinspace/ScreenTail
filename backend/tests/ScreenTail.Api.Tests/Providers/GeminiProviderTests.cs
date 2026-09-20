using System.Net;
using System.Text;
using System.Text.Json;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// What we actually send to Gemini, and what we believe it cost (ST-063).
///
/// Every case here is a defect found by pointing the built provider at the real API on 2026-09-19 rather
/// than at a stub. The stubs had all passed: they agreed with the code about a model name Google had
/// retired, and about a token count that no longer describes the bill. A fake is only ever as right as
/// the assumption it was written from, which is why these tests pin the assumptions themselves.
/// </summary>
public sealed class GeminiProviderTests
{
    [Fact]
    public async Task TheModelIsAskedForByTheNameThisDeploymentConfigured()
    {
        // The name was a constant in the provider until Google retired it, at which point every draft in
        // every deployment became a 404 that only a code change and a release could fix. Model names are
        // Google's to expire, so which one we ask for is a setting.
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler, options => options.Model = "gemini-9.9-flash");

        _ = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Contains(
            "models/gemini-9.9-flash:generateContent",
            handler.Uri!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatIsNotThereNamesTheModelAndTheSettingToChange()
    {
        // The 404 Google returns for a retired model. "This is a bug in ScreenTail" is the wrong thing to
        // tell an operator who can fix it in one setting, and a retirement is announced months ahead:
        // whoever reads this should be pointed at the knob rather than at us.
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = Provider(handler, options => options.Model = "gemini-2.0-flash");

        var result = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.Invalid, result.Error!.Kind);
        Assert.Contains("gemini-2.0-flash", result.Error.What, StringComparison.Ordinal);
        Assert.Contains("Summarization__Model", result.Error.Todo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThinkingIsChargedForLikeAnyOtherOutput()
    {
        // Gemini 3.x reasons before it answers and bills for the reasoning. A one-word question on
        // 2026-09-19 was charged 92 thinking tokens against a single token of answer, so a cost built
        // from candidatesTokenCount alone under-reports by most of the bill — and the daily cap that
        // reads it is then a cap on nothing.
        var thinking = await CostOf(prompt: 1_000, output: 200, thoughts: 5_000);
        var spelledOut = await CostOf(prompt: 1_000, output: 5_200, thoughts: 0);

        Assert.Equal(spelledOut, thinking);
    }

    [Fact]
    public async Task AnAnswerWithNoThinkingCostsOnlyWhatItSaid()
    {
        var quiet = await CostOf(prompt: 1_000, output: 200, thoughts: 0);
        var loud = await CostOf(prompt: 1_000, output: 200, thoughts: 5_000);

        Assert.True(quiet < loud, "Thinking tokens have to move the number or they are not being counted.");
    }

    [Fact]
    public async Task AFrameTravelsAsTheKindOfImageItActuallyIs()
    {
        // The type was hardcoded to JPEG because the Windows client encodes JPEG. That made the provider
        // assert something about bytes it did not produce, and it is the frame that knows.
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler);
        var bundle = Bundle() with
        {
            Frames = [new BundleFrame("f1", 1_000, "Services") { Image = "QUJD", MediaType = "image/png" }],
        };

        _ = await provider.DraftAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Equal("image/png", MimeTypes(handler.Body!).Single());
    }

    [Fact]
    public async Task AFrameThatDoesNotSaySoIsTheJpegTheClientSends()
    {
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler);
        var bundle = Bundle() with
        {
            Frames = [new BundleFrame("f1", 1_000, "Services") { Image = "QUJD" }],
        };

        _ = await provider.DraftAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Equal("image/jpeg", MimeTypes(handler.Body!).Single());
    }

    [Fact]
    public async Task PicturesAreSentSmallUnlessTheDeploymentSaysOtherwise()
    {
        // Measured on 2026-09-19 against gemini-3.6-flash, on the heaviest bundle a client may send
        // (25 frames):
        //
        //   default   39.9 s   31,150 prompt tokens (27,500 of them pictures)   $0.055
        //   medium    21.3 s   16,825 prompt tokens                            $0.029
        //   low       10.5 s   10,250 prompt tokens ( 6,600 of them pictures)  $0.015
        //
        // The default misses ST-063's 30-second budget outright. Low makes it with room to spare, and
        // costs a seventh as much.
        //
        // It costs us little, because the model is not reading these pictures for their text: every
        // frame arrives with the redaction worker's own OCR beside it, already masked, and that is what
        // the note is built from. The picture is there for layout and context. Sending it small also
        // means the model sees less of whatever OCR missed, which is the right direction for INV-1.
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler);

        _ = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Equal("MEDIA_RESOLUTION_LOW", Generation(handler.Body!).GetProperty("mediaResolution").GetString());
    }

    [Fact]
    public async Task ADeploymentThatWantsDetailCanPayForIt()
    {
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler, options => options.MediaResolution = "MEDIA_RESOLUTION_HIGH");

        _ = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Equal("MEDIA_RESOLUTION_HIGH", Generation(handler.Body!).GetProperty("mediaResolution").GetString());
    }

    [Fact]
    public async Task ABlankResolutionLeavesTheChoiceToTheProvider()
    {
        // An escape hatch that costs nothing to keep: a provider that renames or drops the setting
        // should not need a release to stop us sending it.
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler, options => options.MediaResolution = string.Empty);

        _ = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.False(Generation(handler.Body!).TryGetProperty("mediaResolution", out _));
    }

    [Fact]
    public async Task TheKeyIsAHeaderAndNeverInTheUrl()
    {
        // A key in a query string is a key in every proxy log, access log and error report between here
        // and Google. Asserted rather than commented, because it is one character of a refactor away.
        var handler = new RecordingHandler(Answer());
        var provider = Provider(handler, options => options.ApiKey = "sekrit-key-value");

        _ = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("sekrit-key-value", handler.Uri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("sekrit-key-value", Assert.Single(handler.Headers!.GetValues("x-goog-api-key")));
    }

    [Fact]
    public async Task WhenTheProviderSaysWhenToComeBackWeKeepTheNumber()
    {
        // Google answers a rate limit with a google.rpc.RetryInfo in the error details and no Retry-After
        // header, so the only place the number exists is the body. ProviderError has carried a RetryAfter
        // field since the first provider was written and nothing had ever filled it in — which left
        // ST-064's outbox guessing at a delay the provider had already told us.
        var handler = new RecordingHandler(RateLimited("26s"));
        var provider = Provider(handler);

        var result = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Unavailable, result.Error!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(26), result.Error.RetryAfter);
    }

    [Fact]
    public async Task AFractionOfASecondIsNotRoundedAway()
    {
        // Protobuf durations are written "26.656292589s". Parsing only the integer part would turn a
        // sub-second delay into no delay at all, and a retry with no delay is the request that got us
        // rate limited.
        var handler = new RecordingHandler(RateLimited("0.5s"));
        var provider = Provider(handler);

        var result = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(0.5), result.Error!.RetryAfter);
    }

    [Fact]
    public async Task ARateLimitWithNoAdviceSaysSoRatherThanGuessing()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var provider = Provider(handler);

        var result = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderErrorKind.Unavailable, result.Error!.Kind);
        Assert.Null(result.Error.RetryAfter);
    }

    private static HttpResponseMessage RateLimited(string retryDelay) =>
        new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                $$"""
                { "error": { "code": 429, "status": "RESOURCE_EXHAUSTED", "details": [
                    { "@type": "type.googleapis.com/google.rpc.Help", "links": [] },
                    { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "{{retryDelay}}" } ] } }
                """,
                Encoding.UTF8,
                "application/json"),
        };

    /// <summary>Runs one draft against a stubbed usage block and returns what the provider thought it cost.</summary>
    private static async Task<decimal> CostOf(int prompt, int output, int thoughts)
    {
        var handler = new RecordingHandler(Answer(prompt, output, thoughts));
        var provider = Provider(handler);

        var result = await provider.DraftAsync(Bundle(), null, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Error?.ToString());
        return result.Value!.CostUsd;
    }

    private static JsonElement Generation(string body)
    {
        var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("generationConfig").Clone();
    }

    private static IEnumerable<string> MimeTypes(string body)
    {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")
            .EnumerateArray()
            .Where(part => part.TryGetProperty("inline_data", out _))
            .Select(part => part.GetProperty("inline_data").GetProperty("mime_type").GetString()!)];
    }

    [Fact]
    public async Task AnImageIsNotMadeLargerOnItsWayOut()
    {
        // 2026-09-20 review. The default encoder escapes every '+' as six characters, because it is
        // written for JSON that might be pasted into a web page. Base64 is one '+' in sixty-four: a
        // six-megabyte request went out eight per cent larger than it came in, for a reader that is an
        // API and not a browser.
        var handler = new RecordingHandler(Answer());
        var bundle = Bundle() with
        {
            Frames = [new BundleFrame("f1", 1_000, "Services") { Image = "++//QUJD" }],
        };

        _ = await Provider(handler).DraftAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Contains("\"++//QUJD\"", handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelReadsWhatTheTechnicianSaidAndNotItsEscapeCodes()
    {
        // The session travels as text inside the request, so it is encoded twice. With the default
        // encoder the model was shown don\u0027t and caf\u00E9: more tokens to pay for, and a transcript
        // the note is later checked against quoting faithfully that no longer reads like the original.
        var handler = new RecordingHandler(Answer());
        var bundle = Bundle() with
        {
            Transcript = [new BundleSegment("t1", 1_200, "it doesn't print at the café")],
        };

        _ = await Provider(handler).DraftAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Contains("it doesn't print at the café", SessionText(handler.Body!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextThatLooksLikeJsonStaysInsideItsString()
    {
        // Relaxed is not unescaped. A quote or a backslash on a customer's screen still has to arrive
        // as part of the text it was in, or OCR of the right window rewrites the request around it.
        var handler = new RecordingHandler(Answer());
        var hostile = "\",\"frames\":[],\"x\":\"\\";
        var bundle = Bundle() with { Frames = [new BundleFrame("f1", 1_000, hostile)] };

        _ = await Provider(handler).DraftAsync(bundle, null, TestContext.Current.CancellationToken);

        using var session = JsonDocument.Parse(SessionText(handler.Body!));
        var frame = Assert.Single(session.RootElement.GetProperty("frames").EnumerateArray());
        Assert.Equal(hostile, frame.GetProperty("ocr_text").GetString());
    }

    /// <summary>The part of the request that carries the session, as the model is given it.</summary>
    private static string SessionText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("contents")[0].GetProperty("parts")
            .EnumerateArray()
            .Where(part => part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString()!)
            .Last();
    }

    private static GeminiProvider Provider(RecordingHandler handler, Action<SummarizationOptions>? configure = null)
    {
        var options = new SummarizationOptions { ApiKey = "test-key" };
        configure?.Invoke(options);

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
        return new GeminiProvider(http, options, "the note prompt");
    }

    private static HttpResponseMessage Answer(int prompt = 10, int output = 10, int thoughts = 0) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "candidates": [ { "content": { "parts": [ { "text": {{JsonSerializer.Serialize(Draft())}} } ] } } ],
                  "usageMetadata": {
                    "promptTokenCount": {{prompt}},
                    "candidatesTokenCount": {{output}},
                    "thoughtsTokenCount": {{thoughts}}
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

    private static string Draft() => """
        {"problem": "p", "steps": [], "result": "r", "follow_ups": [], "suggested_title": "t",
         "suggested_time_minutes": 10, "kb_candidate": false, "kb_reason": "k", "source": "cloud",
         "prompt_version": "note_v1"}
        """;

    private static SummarizeBundle Bundle() => new()
    {
        SessionId = "s1",
        DurationMs = 20 * 60 * 1000,
        Frames = [new BundleFrame("f1", 1_000, "Services Print Spooler Stopped")],
        Transcript = [new BundleSegment("t1", 1_200, "clearing the queue now")],
    };

    /// <summary>Keeps the request rather than sending it, so the tests can read what we would have posted.</summary>
    private sealed class RecordingHandler(HttpResponseMessage reply) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Body { get; private set; }

        public System.Net.Http.Headers.HttpRequestHeaders? Headers { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Headers = request.Headers;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return reply;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reply.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
