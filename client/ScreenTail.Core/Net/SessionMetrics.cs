using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Net;

/// <summary>
/// One session's metric as the client posts it: <c>shared/contracts/session-metric.v1.json</c> (ST-098).
/// Counts and times only. The one string is the session's id; there is deliberately no other, because a
/// string field is where a title or a company would arrive (INV-10). This list is what Settings shows
/// beside the telemetry toggle.
/// </summary>
public sealed record SessionMetricWire(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("frames")] int Frames,
    [property: JsonPropertyName("transcript_segments")] int TranscriptSegments,
    [property: JsonPropertyName("frames_purged_unredacted")] long FramesPurgedUnredacted,
    [property: JsonPropertyName("edit_ratio")] double? EditRatio,
    [property: JsonPropertyName("published")] bool Published)
{
    public static SessionMetricWire From(Session session, bool published, double? editRatio)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new SessionMetricWire(
            session.SessionId,
            session.StartedAt,
            session.DurationMs ?? 0,
            session.Frames.Count,
            session.Transcript.Count,
            session.FramesPurgedUnredacted,
            editRatio,
            published);
    }
}

/// <summary>
/// How much of the draft the technician changed before publishing: the share of steps that were
/// edited, added or removed, against the longer of the two lists. Zero is a draft published as it
/// came; one is a note with nothing of the draft left. The eval harness's edit rate, on real notes.
/// </summary>
public static class EditRate
{
    public static double Of(DraftNote original, DraftNote published)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(published);
        var before = original.Steps.Select(s => s.Text.Trim()).ToList();
        var after = published.Steps.Select(s => s.Text.Trim()).ToList();
        var length = Math.Max(before.Count, after.Count);
        if (length == 0)
        {
            return 0;
        }

        var changed = 0;
        for (var i = 0; i < length; i++)
        {
            if (i >= before.Count || i >= after.Count || !string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return changed / (double)length;
    }
}

/// <summary>Posts a queued metric. A backend that is away is retried by the outbox; one that refuses is not.</summary>
public sealed class MetricsSender(HttpClient http, Func<string?> token)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _token = token ?? throw new ArgumentNullException(nameof(token));

    public async Task<SendOutcome> SendAsync(OutboxItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_http.BaseAddress is null)
        {
            return SendOutcome.Failed("No backend is configured.");
        }

        if (_token() is not { Length: > 0 } bearer)
        {
            return SendOutcome.Retry("This device is not enrolled yet.");
        }

        using var request = EgressRequest.For(HttpMethod.Post, new Uri("v1/metrics/sessions", UriKind.Relative), EgressPurpose.Backend);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new StringContent(item.Payload, System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return SendOutcome.Retry("The backend did not answer.");
        }
        catch (EgressBlockedException blocked)
        {
            return SendOutcome.Failed(blocked.Message);
        }

        using (response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent or HttpStatusCode.OK => SendOutcome.Done(null),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SendOutcome.Failed("This device may not send metrics."),
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => SendOutcome.Failed("The backend refused the metric."),
                _ => SendOutcome.Retry($"The backend answered {(int)response.StatusCode}."),
            };
        }
    }
}

/// <summary>Told when a session was drafted and when it was published; queues the metric, or nothing when telemetry is off.</summary>
public interface IMetricsReporter
{
    Task ReportAsync(string sessionId, bool published, CancellationToken ct = default);
}

/// <summary>
/// Builds the metric from the store and queues it through the outbox (ST-098). Telemetry off means
/// nothing is queued, not a queued item that is never sent (AC2). The edit ratio compares the draft
/// as first written with the draft as published; a session drafted but not yet published has none.
/// </summary>
public sealed class MetricsReporter(ISessionStore store, Outbox.Outbox outbox, Func<bool> telemetryOn) : IMetricsReporter
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Outbox.Outbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly Func<bool> _telemetryOn = telemetryOn ?? throw new ArgumentNullException(nameof(telemetryOn));

    public async Task ReportAsync(string sessionId, bool published, CancellationToken ct = default)
    {
        if (!_telemetryOn())
        {
            return;
        }

        var session = await _store.LoadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        double? editRatio = null;
        if (published && session.Draft is { } edited && await _store.LoadOriginalDraftAsync(sessionId, ct).ConfigureAwait(false) is { } original)
        {
            editRatio = EditRate.Of(original, edited);
        }

        var metric = SessionMetricWire.From(session, published, editRatio);
        _ = await _outbox.EnqueueAsync(
            new NewOutboxItem(sessionId, OutboxKind.Metric, $"metric:{sessionId}:{(published ? "published" : "drafted")}", JsonSerializer.Serialize(metric, MetricsSender.Json)),
            ct).ConfigureAwait(false);
    }
}
