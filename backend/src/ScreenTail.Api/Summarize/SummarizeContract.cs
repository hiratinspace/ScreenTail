using System.Text.Json.Serialization;

namespace ScreenTail.Api.Summarize;

/// <param name="OcrText">What the redaction worker could read after masking. Never raw screen text.</param>
/// <param name="Excluded">The technician removed this frame in Review (ST-075).</param>
public sealed record BundleFrame(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ts_ms")] long TsMs,
    [property: JsonPropertyName("ocr_text")] string? OcrText,
    [property: JsonPropertyName("excluded_by_user")] bool Excluded = false)
{
    /// <summary>The image, base64, as the client sent it. Held for one request and never written (INV-7).</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }
}

public sealed record BundleSegment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ts_ms")] long TsMs,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// One session, as the client sends it to be drafted (ST-060, ST-063).
///
/// The backend's own shape rather than a shared type: the client is Windows-only and this project must
/// not depend on it. What binds them is the JSON, which the client's <c>SessionBundle</c> writes and
/// this reads, and the field names are the contract.
///
/// <b>Nothing here is stored.</b> It lives for the length of one request, is handed to the model, and is
/// released (INV-7). There is no repository for it and no table it could go in.
/// </summary>
public sealed record SummarizeBundle
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("partial_capture")]
    public bool PartialCapture { get; init; }

    [JsonPropertyName("frames_purged_unredacted")]
    public long FramesPurgedUnredacted { get; init; }

    [JsonPropertyName("ocr_partial")]
    public bool OcrPartial { get; init; }

    [JsonPropertyName("frames")]
    public IReadOnlyList<BundleFrame> Frames { get; init; } = [];

    [JsonPropertyName("transcript")]
    public IReadOnlyList<BundleSegment> Transcript { get; init; } = [];
}

/// <param name="Confidence">
/// <c>high</c> or <c>low</c>. A step nobody spoke about can only be <c>low</c>: Review shows those with a
/// warning the technician has to clear, and claiming high confidence removes the one signal telling them
/// to look closer.
/// </param>
public sealed record DraftStepJson
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("frame_refs")]
    public IReadOnlyList<string> FrameRefs { get; init; } = [];

    [JsonPropertyName("transcript_refs")]
    public IReadOnlyList<string> TranscriptRefs { get; init; } = [];
}

/// <summary>What the model returns, exactly as <c>research/prompts/schema.json</c> defines it.</summary>
public sealed record DraftJson
{
    [JsonPropertyName("problem")]
    public required string Problem { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<DraftStepJson> Steps { get; init; } = [];

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("follow_ups")]
    public IReadOnlyList<string> FollowUps { get; init; } = [];

    [JsonPropertyName("suggested_title")]
    public required string SuggestedTitle { get; init; }

    [JsonPropertyName("suggested_time_minutes")]
    public required long SuggestedTimeMinutes { get; init; }

    [JsonPropertyName("kb_candidate")]
    public required bool KbCandidate { get; init; }

    [JsonPropertyName("kb_reason")]
    public required string KbReason { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("prompt_version")]
    public required string PromptVersion { get; init; }
}
