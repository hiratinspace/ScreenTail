using System.Text.Json.Serialization;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Publish;

/// <summary>One included screenshot, redacted on the device, as base64. Held for this request only (INV-7).</summary>
public sealed record PublishFrame(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("media_type")] string MediaType = "image/jpeg");

/// <summary>
/// One publish as the client posts it (ST-093, ST-094): the note as edited in Review, the frames the
/// technician kept, the time entry, and which destinations to send. <c>shared/contracts/publish-request.v1.json</c>
/// is the shape both ends test against.
/// </summary>
public sealed record PublishBundle
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("ticket_id")]
    public required string TicketId { get; init; }

    /// <summary><c>internal</c> or <c>discussion</c>. Internal by default in the client (v0.4.1 Q2).</summary>
    [JsonPropertyName("note_type")]
    public string NoteType { get; init; } = "internal";

    [JsonPropertyName("minutes")]
    public int Minutes { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("billable")]
    public bool Billable { get; init; } = true;

    /// <summary>Who reviewed it, for the footer. A display name, never an email.</summary>
    [JsonPropertyName("reviewer")]
    public string? Reviewer { get; init; }

    /// <summary>"Drafted with ScreenTail, reviewed by …" at the foot of the note. Configurable (ST-093 AC3).</summary>
    [JsonPropertyName("footer")]
    public bool Footer { get; init; } = true;

    /// <summary><c>ticket_note</c>, <c>time_entry</c>, <c>kb_article</c>. Each is answered on its own.</summary>
    [JsonPropertyName("destinations")]
    public IReadOnlyList<string> Destinations { get; init; } = [];

    [JsonPropertyName("note")]
    public required DraftJson Note { get; init; }

    [JsonPropertyName("frames")]
    public IReadOnlyList<PublishFrame> Frames { get; init; } = [];
}

/// <param name="Kind">The provider's error kind, lower case, or <c>not_configured</c>; null when it went.</param>
public sealed record PublishResultJson(
    [property: JsonPropertyName("destination")] string Destination,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("link")] Uri? Link = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("retryable")] bool Retryable = false);

public sealed record PublishResponse([property: JsonPropertyName("results")] IReadOnlyList<PublishResultJson> Results);

public static class Destinations
{
    public const string TicketNote = "ticket_note";
    public const string TimeEntry = "time_entry";
    public const string KbArticle = "kb_article";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { TicketNote, TimeEntry, KbArticle };
}
