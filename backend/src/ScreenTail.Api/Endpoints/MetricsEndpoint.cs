using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Endpoints;

/// <summary>
/// One session's metric as the client posts it: <c>shared/contracts/session-metric.v1.json</c> (ST-098).
/// The one string is the session's id; the shape has no room for content (INV-10).
/// </summary>
public sealed record SessionMetricRequest(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("frames")] int Frames,
    [property: JsonPropertyName("transcript_segments")] int TranscriptSegments,
    [property: JsonPropertyName("frames_purged_unredacted")] long FramesPurgedUnredacted,
    [property: JsonPropertyName("edit_ratio")] double? EditRatio,
    [property: JsonPropertyName("published")] bool Published);

/// <summary>
/// Metrics ingestion for the pilot's numbers (ST-098): one row per session per tenant, replaced on a
/// retry rather than added to. The weekly view over it is in the migration.
/// </summary>
public static class MetricsEndpoint
{
    public static RouteGroupBuilder MapMetrics(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/metrics/sessions", async (SessionMetricRequest request, ClaimsPrincipal caller, ScreenTailContext db, CancellationToken ct) =>
        {
            if (await CallerCheck.ReadAsync(caller, db, ct).ConfigureAwait(false) is not { } who)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 100)
            {
                return Results.BadRequest(new { error = "bad_session_id", message = "A metric names its session by id." });
            }

            if (request.DurationMs < 0 || request.Frames < 0 || request.TranscriptSegments < 0 || request.FramesPurgedUnredacted < 0 || request.EditRatio is < 0 or > 1)
            {
                return Results.BadRequest(new { error = "bad_metric", message = "Counts are not negative and the edit ratio is between 0 and 1." });
            }

            var row = await db.SessionMetrics.SingleOrDefaultAsync(m => m.TenantId == who.TenantId && m.SessionId == request.SessionId, ct).ConfigureAwait(false)
                ?? db.SessionMetrics.Add(new SessionMetric { Id = Guid.NewGuid(), TenantId = who.TenantId, DeviceId = who.DeviceId, SessionId = request.SessionId }).Entity;
            row.DeviceId = who.DeviceId;
            row.StartedAt = request.StartedAt;
            row.DurationMs = request.DurationMs;
            row.Frames = request.Frames;
            row.TranscriptSegments = request.TranscriptSegments;
            row.FramesPurgedUnredacted = request.FramesPurgedUnredacted;
            row.EditRatio = request.EditRatio;
            row.Published = request.Published;
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("PostSessionMetric")
        .WithSummary("Records one session's counts and times. Never content.");

        return group;
    }
}
