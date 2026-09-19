using System.Globalization;
using System.Net;
using System.Text;
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
/// <b>The key is a header, never a query string.</b> Gemini's own documentation offers both, and a key in
/// a URL is a key in every proxy log, every error report and every trace between here and there.
/// </summary>
public sealed class GeminiProvider(HttpClient http, SummarizationOptions options, string prompt) : ILlmProvider
{
    /// <summary>
    /// Roughly what Flash charges, in US dollars per million tokens, as of 2026-09.
    ///
    /// An estimate, and labelled as one: it is used for a cap rather than for an invoice, and being
    /// slightly wrong costs a tenant a few sessions either way rather than money. The provider's own
    /// usage figures are what it is applied to.
    /// </summary>
    private const decimal InputCostPerMillion = 0.075m;

    private const decimal OutputCostPerMillion = 0.30m;

    public string Name => "gemini-flash";

    public async Task<ProviderResult<LlmDraft>> DraftAsync(
        SummarizeBundle bundle,
        string? repair,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "v1beta/models/gemini-2.0-flash:generateContent")
        {
            Content = new StringContent(Body(bundle, repair), Encoding.UTF8, "application/json"),
        };

        // Header rather than query string: a key in a URL is a key in every proxy log between here and
        // Google.
        request.Headers.Add("x-goog-api-key", options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Unreachable or too slow. Retryable, and the only kind that earns a fallback.
            return ProviderResult.Failure<LlmDraft>(new ProviderError(
                ProviderErrorKind.Unavailable,
                "The drafting model did not answer.",
                "The session is queued and will be drafted when it responds."));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return ProviderResult.Failure<LlmDraft>(Failure(response.StatusCode));
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Read(body);
        }
    }

    /// <summary>
    /// Maps the status onto what a technician should do (Spec §4).
    ///
    /// Only 5xx and 429 are worth a second provider. A 401 means the key is wrong, and spending money
    /// somewhere else does not make it right.
    /// </summary>
    private static ProviderError Failure(HttpStatusCode status) => status switch
    {
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

    /// <summary>Pulls the text and the usage out of Gemini's envelope.</summary>
    private static ProviderResult<LlmDraft> Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var text = root.GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                return ProviderResult.Failure<LlmDraft>(new ProviderError(
                    ProviderErrorKind.Invalid,
                    "The drafting model returned an empty answer.",
                    "The session is queued and will be tried again."));
            }

            var cost = 0m;
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                cost = Cost(
                    usage.TryGetProperty("promptTokenCount", out var input) ? input.GetInt32() : 0,
                    usage.TryGetProperty("candidatesTokenCount", out var output) ? output.GetInt32() : 0);
            }

            return ProviderResult.Success(new LlmDraft(text, cost));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            return ProviderResult.Failure<LlmDraft>(new ProviderError(
                ProviderErrorKind.Invalid,
                "The drafting model's answer was not in the shape it documents.",
                "This is a bug in ScreenTail rather than something you can fix."));
        }
    }

    internal static decimal Cost(int promptTokens, int completionTokens) =>
        ((promptTokens * InputCostPerMillion) + (completionTokens * OutputCostPerMillion)) / 1_000_000m;

    /// <summary>
    /// The request body: the prompt, the session, and the images.
    ///
    /// Images go as inline data rather than as an upload. They are already redacted and already small
    /// after downscaling, and an upload would mean a customer's screenshot sitting in someone else's
    /// object store with its own retention.
    /// </summary>
    private string Body(SummarizeBundle bundle, string? repair)
    {
        var parts = new List<object> { new { text = prompt } };

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
            }),
        });

        foreach (var frame in bundle.Frames.Where(frame => !string.IsNullOrEmpty(frame.Image)))
        {
            parts.Add(new { inline_data = new { mime_type = "image/jpeg", data = frame.Image } });
        }

        return JsonSerializer.Serialize(new
        {
            contents = new[] { new { role = "user", parts } },
            generationConfig = new
            {
                // Asked for as JSON rather than parsed out of prose. It does not remove the need for the
                // post-conditions — a schema-valid draft can still cite a frame that does not exist —
                // but it removes the whole class of failure where the answer is a paragraph.
                responseMimeType = "application/json",

                // Low, not zero. This is a report of what happened, and there is nothing to be creative
                // about; zero is not offered as meaningfully different and costs a re-roll of nothing.
                temperature = 0.2,
            },
        });
    }
}
