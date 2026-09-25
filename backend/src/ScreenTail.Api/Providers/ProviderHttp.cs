using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ScreenTail.Api.Providers;

/// <summary>How patient a provider is with a rate limit or an outage: the same rule for every one (ST-091 AC2).</summary>
public sealed record RetryPolicy(TimeSpan BackoffBase, int MaxAttempts = 3);

/// <summary>
/// Sends a request, retries what is worth retrying, and turns the answer into a value. Shared by every
/// provider so that ConnectWise and Hudu cannot disagree about what a 429 means.
///
/// Only <see cref="ProviderErrorKind.Unavailable"/> is retried, with exponential backoff and jitter,
/// three attempts at most; the request is built fresh per attempt because an
/// <see cref="HttpRequestMessage"/> cannot be sent twice. A wrong key is sent once.
/// </summary>
public static class ProviderHttp
{
    public static async Task<ProviderResult<JsonElement>> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> build,
        RetryPolicy retry,
        Func<HttpStatusCode, string, RetryConditionHeaderValue?, ProviderError> failure,
        string providerName,
        TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(time);

        ProviderError? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, retry.MaxAttempts); attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(Backoff(retry.BackoffBase, attempt), time, ct).ConfigureAwait(false);
            }

            HttpResponseMessage response;
            try
            {
                using var request = build();
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                last = new ProviderError(ProviderErrorKind.Unavailable, $"{providerName} did not answer.", "Try again in a minute.");
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return ProviderResult.Success(Parse(body));
                }

                last = failure(response.StatusCode, body, response.Headers.RetryAfter);
                if (!last.Retryable)
                {
                    return ProviderResult.Failure<JsonElement>(last);
                }
            }
        }

        return ProviderResult.Failure<JsonElement>(last!);
    }

    /// <summary>The wait a rate-limited answer asked for, when it said.</summary>
    public static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
    }

    /// <summary>One line of the provider's own words, bounded, with no trailing stop: what a technician may paste into a ticket, and nothing else from the body.</summary>
    public static string Sanitise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no reason given";
        }

        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (oneLine.Length > 200)
        {
            oneLine = oneLine[..200];
        }

        return oneLine.TrimEnd('.');
    }

    private static TimeSpan Backoff(TimeSpan baseDelay, int attempt)
    {
        var baseMs = baseDelay.TotalMilliseconds;
        var exponential = baseMs * Math.Pow(2, attempt - 2);
        var jitter = baseMs > 0 ? Random.Shared.NextDouble() * baseMs : 0;
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }

    private static JsonElement Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
