using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Providers.Llm;

/// <summary>
/// Gemini Flash (ST-063).
///
/// The default because it is the cheapest of the three at this shape of request, it reads images
/// natively, and the scope document budgets under ten cents a session. Nothing in
/// <see cref="SummarizationService"/> knows it is running: swapping it costs this one class.
///
/// <b>Which Flash is a setting, not a constant.</b> This class speaks the family's wire format; the
/// version comes from <see cref="SummarizationOptions.Model"/>, because Google retires model names on a
/// schedule and the first build of this one was already asking for a name that had gone.
///
/// <b>The key is a header, never a query string.</b> Gemini's own documentation offers both, and a key in
/// a URL is a key in every proxy log, every error report and every trace between here and there.
/// </summary>
public sealed class GeminiProvider(HttpClient http, SummarizationOptions options, string prompt) : ILlmProvider
{
    /// <summary>
    /// Which model answered. The configured name rather than the family, because "gemini-flash" in a
    /// cost row cannot tell you which of six models with that word in the name ran up the bill.
    /// </summary>
    public string Name => options.Model;

    /// <summary>
    /// How this class writes JSON: escaping what JSON requires and nothing more.
    ///
    /// The default encoder also escapes everything that could matter inside a web page — '+', the
    /// apostrophe, every accented letter. None of this is ever put in a page. It is posted to an API, and
    /// the cost of the caution was real: base64 is one '+' in sixty-four, so every image went out eight
    /// per cent larger, and the model was shown don\u0027t where the technician said don't.
    ///
    /// Quotes, backslashes and control characters are still escaped, which is what keeps text from a
    /// customer's screen inside the string it arrived in. The name says "unsafe" about HTML, not JSON.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<ProviderResult<LlmDraft>> DraftAsync(
        SummarizeBundle bundle,
        string? repair,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // A caller that has already given up is told so before anything is built or sent. A fallback
        // asked after the primary used the whole deadline lands here, and owes nothing.
        if (ct.IsCancellationRequested)
        {
            return ProviderResult.Failure<LlmDraft>(Unanswered(inTime: true, mayHaveBeenBilled: false));
        }

        var content = new OutgoingContent(Body(bundle, repair))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"v1beta/models/{options.Model}:generateContent")
        {
            Content = content,
        };

        // Header rather than query string: a key in a URL is a key in every proxy log between here and
        // Google.
        request.Headers.Add("x-goog-api-key", options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Retryable either way. Whether it cost anything is decided by whether the request left, and
            // by nothing else (2026-10-12 review). This used to ask which token had fired, and in the
            // deployed wiring the service's own deadline always fires first: every timeout was settled
            // at nothing, the client retried it, and the daily cap never moved during the slowdown it
            // exists for. A caller that hung up after sending, a dropped connection after sending and
            // either deadline are all a request the model may have run. A connection that never opened
            // is not, so an outage still cannot spend a tenant's day.
            return ProviderResult.Failure<LlmDraft>(
                Unanswered(inTime: ex is OperationCanceledException, mayHaveBeenBilled: content.Left));
        }

        using (response)
        {
            // Already in memory: SendAsync reads the whole answer before it returns. Not the caller's
            // token, because a deadline landing between the two lines would throw away an answer that
            // has been paid for, and the cost in it, for a read that cannot block.
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? Read(body)
                : ProviderResult.Failure<LlmDraft>(
                    Failure(response.StatusCode, options.Model) with { RetryAfter = RetryAfter(body) });
        }
    }

    /// <summary>
    /// Nobody answered. Retryable, and the only kind that earns a fallback; billed or not is the caller's
    /// question to settle, from <see cref="ProviderError.MayHaveBeenBilled"/>.
    /// </summary>
    private static ProviderError Unanswered(bool inTime, bool mayHaveBeenBilled) => new(
        ProviderErrorKind.Unavailable,
        inTime ? "The drafting model did not answer in time." : "The drafting model did not answer.",
        "The session is queued and will be drafted when it responds.")
    {
        MayHaveBeenBilled = mayHaveBeenBilled,
    };

    /// <summary>
    /// Maps the status onto what a technician should do (Spec §4).
    ///
    /// Only 5xx and 429 are worth a second provider. A 401 means the key is wrong, and spending money
    /// somewhere else does not make it right.
    /// </summary>
    private static ProviderError Failure(HttpStatusCode status, string model) => status switch
    {
        // A retired model. Google announces these months ahead and answers with a 404 naming the
        // replacement, so whoever reads this can end the outage with one setting rather than waiting for
        // a release. The first build of this provider asked for gemini-2.0-flash and found out this way.
        HttpStatusCode.NotFound => new ProviderError(
            ProviderErrorKind.Invalid,
            $"The drafting model \"{model}\" is not available to this deployment.",
            "Set Summarization__Model to a model the provider still serves."),

        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderError(
            ProviderErrorKind.Unauthenticated,
            "The drafting model rejected this deployment's API key.",
            "Check the Summarization__ApiKey setting on the server."),

        HttpStatusCode.TooManyRequests => new ProviderError(
            ProviderErrorKind.Unavailable,
            "The drafting model is rate limiting this deployment.",
            "The session is queued and will be drafted shortly."),

        >= HttpStatusCode.InternalServerError => new ProviderError(
            ProviderErrorKind.Unavailable,
            "The drafting model returned an error.",
            "The session is queued and will be drafted when it recovers."),

        _ => new ProviderError(
            ProviderErrorKind.Invalid,
            $"The drafting model refused the request ({(int)status}).",
            "This is a bug in ScreenTail rather than something you can fix."),
    };

    /// <summary>
    /// Pulls the usage and the text out of Gemini's envelope, in that order.
    ///
    /// <b>The bill is read first.</b> A 200 is a model that ran, and the usage block is what it cost
    /// whether or not anything usable came back: an empty answer, a prompt the provider blocked and a
    /// completion cut off before its first word are all on the invoice. This used to read the text first
    /// and give up before it reached the usage, so each of those settled at nothing (2026-10-12 review).
    /// A 200 whose bill cannot be read is charged the estimate, the same rule as a call we stopped
    /// waiting for: an over-count costs a tenant one draft against the cap, an under-count is the cap
    /// not working.
    /// </summary>
    private ProviderResult<LlmDraft> Read(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return ProviderResult.Failure<LlmDraft>(Unreadable(cost: null));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ProviderResult.Failure<LlmDraft>(Unreadable(cost: null));
            }

            decimal? cost = root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? Cost(
                    Tokens(usage, "promptTokenCount"),

                    // Thinking is billed at the output rate and is *not* part of the answer's own count:
                    // the provider documents totalTokenCount as prompt plus thoughts plus candidates,
                    // three separate addends. A one-word question on 2026-09-19 was charged 92 thinking
                    // tokens against a single token of answer, so counting only the answer under-reports
                    // by most of the bill, and the daily cap reading it would be a cap on nothing.
                    Tokens(usage, "candidatesTokenCount") + Tokens(usage, "thoughtsTokenCount"))
                : null;

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            {
                // No candidates at all is how a blocked prompt is answered; anything else here is a shape
                // nobody documented. Either way the model was asked and may have been paid.
                return ProviderResult.Failure<LlmDraft>(root.TryGetProperty("promptFeedback", out _)
                    ? Empty(cost)
                    : Unreadable(cost));
            }

            var text = FirstText(candidates);
            if (string.IsNullOrWhiteSpace(text))
            {
                return ProviderResult.Failure<LlmDraft>(Empty(cost));
            }

            return ProviderResult.Success(new LlmDraft(text, cost ?? options.MaxSessionCostUsd));
        }
    }

    /// <summary>The first candidate's first part's text, or null when the answer has none.</summary>
    private static string? FirstText(JsonElement candidates)
    {
        if (candidates.GetArrayLength() == 0
            || candidates[0] is not { ValueKind: JsonValueKind.Object } candidate
            || !candidate.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array
            || parts.GetArrayLength() == 0
            || parts[0] is not { ValueKind: JsonValueKind.Object } part
            || !part.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return text.GetString();
    }

    /// <summary>The model answered with nothing to show. Billed, at what its usage says when it said.</summary>
    private static ProviderError Empty(decimal? cost) => new(
        ProviderErrorKind.Invalid,
        "The drafting model returned an empty answer.",
        "The session is queued and will be tried again.")
    {
        MayHaveBeenBilled = true,
        CostUsd = cost,
    };

    /// <summary>The model answered in a shape we cannot read. Billed all the same: it was a 200.</summary>
    private static ProviderError Unreadable(decimal? cost) => new(
        ProviderErrorKind.Invalid,
        "The drafting model's answer was not in the shape it documents.",
        "This is a bug in ScreenTail rather than something you can fix.")
    {
        MayHaveBeenBilled = true,
        CostUsd = cost,
    };

    /// <summary>
    /// When the provider said to come back, if it did.
    ///
    /// Google puts it in a <c>google.rpc.RetryInfo</c> among the error details and sends no
    /// <c>Retry-After</c> header, so the body is the only place it exists. Worth reading: a rate limit
    /// answered with our own guess is either a retry too early, which is what caused it, or a session
    /// sitting in the outbox long after the window reopened.
    /// </summary>
    private static TimeSpan? RetryAfter(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.TryGetProperty("@type", out var type)
                    && type.GetString()?.EndsWith("google.rpc.RetryInfo", StringComparison.Ordinal) == true
                    && detail.TryGetProperty("retryDelay", out var delay))
                {
                    return Duration(delay.GetString());
                }
            }
        }
        catch (JsonException)
        {
            // An error body we cannot read is still an error. The caller has a usable failure already.
        }

        return null;
    }

    /// <summary>
    /// A protobuf duration: seconds, a decimal point and a trailing "s" — <c>26s</c>, <c>26.656292589s</c>.
    /// Parsed whole, because rounding "0.5s" down to nothing produces a retry with no delay, which is
    /// the request that earned the rate limit in the first place.
    /// </summary>
    private static TimeSpan? Duration(string? value)
    {
        if (value is null || !value.EndsWith('s'))
        {
            return null;
        }

        return double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    /// <summary>
    /// One count out of the usage block. A count that is not a whole non-negative number is read as none,
    /// rather than thrown on after the call has been paid for.
    /// </summary>
    private static int Tokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count)
            && count > 0
                ? count
                : 0;

    /// <summary>
    /// What the call cost, at this deployment's configured rates.
    ///
    /// An estimate applied to the provider's own token counts, and used for a cap rather than for an
    /// invoice. Being slightly wrong costs a tenant a few sessions either way; being wrong by the whole
    /// thinking budget, as this was, costs money nobody budgeted for.
    /// </summary>
    private decimal Cost(int promptTokens, int completionTokens) =>
        ((promptTokens * options.InputCostPerMillionUsd)
            + (completionTokens * options.OutputCostPerMillionUsd)) / 1_000_000m;

    /// <summary>
    /// The request body, and the one fact about it that money depends on: whether any of it has left.
    ///
    /// A transport writes the body after the connection is open, so the first write is the point past
    /// which the model may have the request. Every way of reading content goes through one of the methods
    /// below (buffering included), so nothing can send it without saying so. When unsure it says yes:
    /// an over-count costs a tenant one draft against the cap, an under-count is the cap not working.
    /// </summary>
    private sealed class OutgoingContent(byte[] body) : HttpContent
    {
        private volatile bool _left;

        public bool Left => _left;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _left = true;
            return stream.WriteAsync(body, cancellationToken).AsTask();
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _left = true;
            stream.Write(body);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    /// <summary>
    /// The request body: the prompt, the session, and the images.
    ///
    /// Images go as inline data rather than as an upload. They are already redacted and already small
    /// after downscaling, and an upload would mean a customer's screenshot sitting in someone else's
    /// object store with its own retention.
    /// </summary>
    private byte[] Body(SummarizeBundle bundle, string? repair)
    {
        var parts = new List<object> { new { text = prompt } };
        if (StyleHints.Paragraph(bundle.StyleHints) is { } style)
        {
            parts.Add(new { text = style });
        }


        if (repair is not null)
        {
            parts.Add(new
            {
                text = "Your previous answer was rejected for these reasons. Return the whole object again, "
                    + $"corrected, and change nothing else:\n{repair}",
            });
        }

        // The session as JSON, minus the images, which travel as parts of their own.
        parts.Add(new
        {
            text = JsonSerializer.Serialize(new
            {
                session_id = bundle.SessionId,
                duration_ms = bundle.DurationMs,
                partial_capture = bundle.PartialCapture,
                frames_purged_unredacted = bundle.FramesPurgedUnredacted,
                ocr_partial = bundle.OcrPartial,
                active_minutes = bundle.DurationMs is { } ms
                    ? Math.Round(ms / 60_000d).ToString(CultureInfo.InvariantCulture)
                    : null,
                frames = bundle.Frames.Select(frame => new { id = frame.Id, ts_ms = frame.TsMs, ocr_text = frame.OcrText }),
                transcript = bundle.Transcript.Select(segment => new { id = segment.Id, ts_ms = segment.TsMs, text = segment.Text }),
            }, Wire),
        });

        foreach (var frame in bundle.Frames.Where(frame => !string.IsNullOrEmpty(frame.Image)))
        {
            parts.Add(new { inline_data = new { mime_type = frame.MediaType, data = frame.Image } });
        }

        var generation = new Dictionary<string, object>
        {
            // Asked for as JSON rather than parsed out of prose. It does not remove the need for the
            // post-conditions — a schema-valid draft can still cite a frame that does not exist — but it
            // removes the whole class of failure where the answer is a paragraph.
            ["responseMimeType"] = "application/json",

            // Low, not zero. This is a report of what happened, and there is nothing to be creative
            // about; zero is not offered as meaningfully different and costs a re-roll of nothing.
            ["temperature"] = 0.2,

            // Output is billed at five times input, and nothing bounded it. A note is a few hundred
            // tokens; a model having a bad day can spend more on one runaway completion than the whole
            // session was reserved for. The ceiling is generous enough that a truncated note means
            // something went wrong rather than that the session was long (2026-09-20 review).
            ["maxOutputTokens"] = options.MaxOutputTokens,
        };

        // Omitted when blank, so an operator can hand the choice back to the provider without a release.
        if (!string.IsNullOrWhiteSpace(options.MediaResolution))
        {
            generation["mediaResolution"] = options.MediaResolution;
        }

        // Straight to the bytes that are sent. Building a string first and encoding it afterwards held
        // the images three times over — about forty megabytes of allocation for a six-megabyte request.
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                contents = new[] { new { role = "user", parts } },
                generationConfig = generation,
            },
            Wire);
    }
}
