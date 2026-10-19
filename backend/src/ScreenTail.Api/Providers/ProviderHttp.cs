using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ScreenTail.Api.Providers;

/// <summary>How patient a provider is with a rate limit or an outage: the same rule for every one (ST-091 AC2).</summary>
public sealed record RetryPolicy(TimeSpan BackoffBase, int MaxAttempts = 3)
{
    /// <summary>The longest single wait a caller will sit through before trying again.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Sends a request, retries what is worth retrying, and turns the answer into a value. Shared by every
/// provider so that ConnectWise and Hudu cannot disagree about what a 429 means.
///
/// Only <see cref="ProviderErrorKind.Unavailable"/> is retried, three attempts at most; the request is
/// built fresh per attempt because an <see cref="HttpRequestMessage"/> cannot be sent twice. A wrong
/// key is sent once. The wait before the next attempt is the exponential backoff with jitter, or the
/// wait the provider asked for when that is longer; a provider that asks for more than the caller can
/// sit through is answered at once with its wait, rather than called again inside its own window.
///
/// <b>A write is different</b> (2026-10-12 review). A note, a time entry, a document or an article that
/// timed out, lost its connection or met a server error after its body left may already be saved, and
/// sending it again is a second one on the customer's ticket. So a write is tried again only when none
/// of its body left this machine, or when the provider said in a 429 that it did not take it; anything
/// else is <see cref="ProviderErrorKind.Unknown"/>, which says to look before publishing again.
///
/// An answer is held in memory only up to the client's limit (<see cref="MaxResponseBytes"/>), and a
/// success that is not JSON is a typed error, never an exception.
/// </summary>
public static class ProviderHttp
{
    /// <summary>
    /// The most of one answer a provider client holds in memory. A tenant chooses its site and the
    /// backend is shared, so an answer that never ends must not be allowed to take the memory every
    /// other tenant is using. A page of tickets or companies is a few kilobytes.
    /// </summary>
    public const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>The deadline and the answer limit every PSA and documentation client has. Program.cs applies it to each named client.</summary>
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = MaxResponseBytes;
    }

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

        var attempts = Math.Max(1, retry.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await AttemptAsync(http, build, failure, providerName, ct).ConfigureAwait(false);
            if (outcome.Again is not { } again)
            {
                return outcome.Done;
            }

            if (attempt >= attempts)
            {
                return ProviderResult.Failure<JsonElement>(again);
            }

            var asked = again.RetryAfter is { } after && after > TimeSpan.Zero ? after : TimeSpan.Zero;
            if (asked > retry.MaxWait)
            {
                return ProviderResult.Failure<JsonElement>(again);
            }

            var backoff = Backoff(retry.BackoffBase, attempt + 1);
            await Task.Delay(backoff > asked ? backoff : asked, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One send: an answer to return as it is, or a failure worth another attempt.</summary>
    private static async Task<Outcome> AttemptAsync(
        HttpClient http,
        Func<HttpRequestMessage> build,
        Func<HttpStatusCode, string, RetryConditionHeaderValue?, ProviderError> failure,
        string providerName,
        CancellationToken ct)
    {
        using var request = build();
        var writes = !IsSafe(request.Method);
        Outgoing? outgoing = null;
        if (writes && request.Content is { } content)
        {
            outgoing = new Outgoing(content);
            request.Content = outgoing;
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (PublicNetwork.WasRefused(ex))
        {
            // Not an outage, so not retried: the site itself is the problem (security check 2026-10-14).
            return Outcome.Failed(new ProviderError(
                ProviderErrorKind.Invalid,
                $"The {providerName} site is on a private network, and ScreenTail only calls public addresses.",
                "Enter the provider's public address in Settings → Integrations."));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // A write with no body to watch is taken to have left: the safe mistake.
            if (writes && (outgoing?.Left ?? true))
            {
                return Outcome.Failed(MayHaveLanded(providerName));
            }

            if (ex is HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded })
            {
                // The client stopped reading at its limit. The site is the problem, and asking again
                // would only read the same answer again.
                return Outcome.Failed(TooLarge(providerName));
            }

            return Outcome.Retry(new ProviderError(ProviderErrorKind.Unavailable, $"{providerName} did not answer.", "Try again in a minute."));
        }

        using (response)
        {
            // Already in memory, and no more than the client's limit: SendAsync buffered it.
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return TryParse(body, out var parsed)
                    ? new Outcome(ProviderResult.Success(parsed), null)
                    : Outcome.Failed(Unreadable(providerName, writes));
            }

            if (writes && response.StatusCode == HttpStatusCode.SeeOther)
            {
                // "Done, look over there" is how a site answers a write it has carried out.
                return Outcome.Failed(MayHaveLanded(providerName));
            }

            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                // Redirects are not followed (security check 2026-10-14), so a moved site lands here.
                // The stored site is what needs changing, which no provider's own mapping would say.
                return Outcome.Failed(new ProviderError(
                    ProviderErrorKind.Invalid,
                    $"The {providerName} site answered with a redirect, and ScreenTail does not follow redirects.",
                    "Check the site address in Settings → Integrations."));
            }

            var failed = failure(response.StatusCode, body, response.Headers.RetryAfter);
            if (!failed.Retryable)
            {
                return Outcome.Failed(failed);
            }

            // A 429 is the provider saying it did not take the request. Any other answer that asks for
            // patience may have come after the write was saved.
            return writes && response.StatusCode != HttpStatusCode.TooManyRequests
                ? Outcome.Failed(MayHaveLanded(providerName) with { RetryAfter = failed.RetryAfter })
                : Outcome.Retry(failed);
        }
    }

    /// <summary>
    /// Reads what a provider answered, or says in a typed error that it could not. A JSON answer in
    /// the wrong shape (no id, an object where a list belongs) is the same kind of problem as one that
    /// is not JSON at all, and is reported the same way rather than thrown.
    /// </summary>
    /// <param name="written">Whether the answer is to a write, which makes an unreadable answer one that may have been saved.</param>
    public static ProviderResult<T> Read<T>(ProviderResult<JsonElement> answer, Func<JsonElement, T> read, string providerName, bool written)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!answer.Ok)
        {
            return ProviderResult.Failure<T>(answer.Error!);
        }

        try
        {
            return ProviderResult.Success(read(answer.Value));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return ProviderResult.Failure<T>(Unreadable(providerName, written));
        }
    }

    /// <summary>A provider's identifier for something: a number as written, or a string that is not empty. Anything else throws, for <see cref="Read{T}"/> to report.</summary>
    public static string Id(JsonElement element)
    {
        var id = element.GetProperty("id");
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(id.GetString()) => id.GetString()!,
            _ => throw new InvalidOperationException("The answer has no usable id."),
        };
    }

    /// <summary>A write that left and was not answered either way. Retrying it is how a ticket gets the note twice.</summary>
    public static ProviderError MayHaveLanded(string providerName) => new(
        ProviderErrorKind.Unknown,
        $"{providerName} did not confirm the request, so it may have been saved.",
        $"Check {providerName} before publishing again, so nothing is added twice.");

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

    /// <param name="Done">The answer, when there is no point in another attempt.</param>
    /// <param name="Again">The failure, when another attempt may work.</param>
    private readonly record struct Outcome(ProviderResult<JsonElement> Done, ProviderError? Again)
    {
        public static Outcome Failed(ProviderError error) => new(ProviderResult.Failure<JsonElement>(error), null);

        public static Outcome Retry(ProviderError error) => new(default, error);
    }

    private static ProviderError Unreadable(string providerName, bool written) => written
        ? new ProviderError(
            ProviderErrorKind.Unknown,
            $"{providerName} accepted the request but its answer could not be read, so it may have been saved.",
            $"Check {providerName} before publishing again, so nothing is added twice.")
        : new ProviderError(
            ProviderErrorKind.Invalid,
            $"The {providerName} site answered with something that is not its API.",
            "Check the site address in Settings → Integrations.");

    private static ProviderError TooLarge(string providerName) => new(
        ProviderErrorKind.Invalid,
        $"The {providerName} site sent an answer larger than ScreenTail accepts.",
        "Check the site address in Settings → Integrations.");

    /// <summary>GET and HEAD read; everything else this code sends writes.</summary>
    private static bool IsSafe(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

    private static TimeSpan Backoff(TimeSpan baseDelay, int attempt)
    {
        var baseMs = baseDelay.TotalMilliseconds;
        var exponential = baseMs * Math.Pow(2, attempt - 2);
        var jitter = baseMs > 0 ? Random.Shared.NextDouble() * baseMs : 0;
        return TimeSpan.FromMilliseconds(exponential + jitter);
    }

    private static bool TryParse(string body, out JsonElement parsed)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            parsed = JsonDocument.Parse("{}").RootElement.Clone();
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            // The body is not quoted back: it may be a sign-in page or anything else the site serves.
            parsed = default;
            return false;
        }
    }

    /// <summary>
    /// A write's body, and the one fact about it a retry depends on: whether any of it has left. A
    /// transport writes the body after the connection is open, so the first write is the point past
    /// which the provider may have the request. Every way of reading content goes through one of the
    /// methods below, buffering included, so nothing can send it without saying so. When unsure it
    /// says yes: the cost of that is a technician asked to look, not a duplicate note.
    /// </summary>
    private sealed class Outgoing : HttpContent
    {
        private readonly HttpContent _inner;
        private volatile bool _left;

        public Outgoing(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers)
            {
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        public bool Left => _left;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _left = true;
            return _inner.CopyToAsync(stream, context, cancellationToken);
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _left = true;
            _inner.CopyTo(stream, context, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            var known = _inner.Headers.ContentLength;
            length = known ?? 0;
            return known.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
