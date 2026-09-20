using System.Buffers;
using System.Buffers.Text;
using System.Text.RegularExpressions;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// What a bundle may be, checked before anything is spent on it (ST-063).
///
/// <b>An authenticated device is not a trusted one.</b> A device is a Windows machine in somebody's
/// office; a compromised one is the ordinary threat rather than an exotic one, and the same shapes arrive
/// from an honest client with a bug in it. Until the 2026-09-19 review the only limit on what one could
/// send was Kestrel's default request size.
///
/// <b>Every check here runs before the model call.</b> Two of the review's findings were the same
/// mistake: the request was accepted, the model was paid, and the failure came afterwards — a
/// <c>session_id</c> longer than the ledger's column, and two frames sharing an id. Both left a billed
/// draft with no ledger row, which is a daily spending cap that never moves. A check that runs after the
/// money is gone is not a check.
///
/// The numbers come from what the client actually sends rather than from what felt safe. The client's
/// own ceiling is <c>BundleOptions.MaxFrames</c> = 25 with a 4 MB byte budget, and ST-063 measured that
/// worst case at 12.2 s and $0.0167. A server that trusts the client to have applied its own ceiling has
/// no ceiling.
/// </summary>
public static partial class BundleLimits
{
    /// <summary>The client's <c>BundleOptions.MaxFrames</c>. Each frame is about 1,100 tokens of input.</summary>
    public const int MaxFrames = 25;

    /// <summary>
    /// Base64 characters across all frames. The client budgets 4 MB of image bytes, which is about 5.6 M
    /// characters encoded; this is twice that, so an honest client is never refused for being close.
    /// </summary>
    public const int MaxImageChars = 12 * 1024 * 1024;

    /// <summary>Ten hours of continuous speech at a segment every few seconds. No session is longer.</summary>
    public const int MaxTranscriptSegments = 4_000;

    /// <summary>One spoken segment. Whisper emits sentences, not essays.</summary>
    public const int MaxTextChars = 10_000;

    /// <summary>The redaction worker's text for one frame. A dense screen of text is a few thousand.</summary>
    public const int MaxOcrChars = 100_000;

    /// <summary><see cref="Data.DraftCost.SessionId"/>'s column width. Longer is billed and then refused by Postgres.</summary>
    public const int MaxSessionIdLength = 64;

    /// <summary>What the Windows client can encode. <c>media_type</c> reaches the provider verbatim.</summary>
    private static readonly string[] Images = ["image/jpeg", "image/png"];

    /// <summary>
    /// Why this bundle may not be drafted, or null when it may.
    ///
    /// A sentence for a technician, in Spec §4's shape, because it comes back in the 400. It names what
    /// was wrong and never quotes the value: an id is opaque, but an OCR string is a customer's screen
    /// and has no business in an error body or a log (INV-10).
    /// </summary>
    public static string? Check(SummarizeBundle? bundle)
    {
        if (bundle is null)
        {
            return "The request had no session in it.";
        }

        if (string.IsNullOrWhiteSpace(bundle.SessionId))
        {
            return "A session id is required.";
        }

        if (bundle.SessionId.Length > MaxSessionIdLength)
        {
            return $"That session id is longer than {MaxSessionIdLength} characters.";
        }

        if (!Identifier().IsMatch(bundle.SessionId))
        {
            return "That session id is not an identifier.";
        }

        if (bundle.DurationMs is < 0)
        {
            // It becomes "active minutes" for the model, and a time entry somebody is billed for.
            return "That session has a negative duration.";
        }

        if (bundle.Frames is null || bundle.Transcript is null)
        {
            return "The frames and transcript must be present, even when empty.";
        }

        if (bundle.Frames.Count > MaxFrames)
        {
            return $"A session may carry at most {MaxFrames} frames.";
        }

        if (bundle.Transcript.Count > MaxTranscriptSegments)
        {
            return $"A session may carry at most {MaxTranscriptSegments} transcript segments.";
        }

        return Frames(bundle) ?? Transcript(bundle);
    }

    private static string? Frames(SummarizeBundle bundle)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var imageChars = 0L;

        foreach (var frame in bundle.Frames)
        {
            if (frame is null || string.IsNullOrWhiteSpace(frame.Id))
            {
                return "Every frame needs an id.";
            }

            if (!ids.Add(frame.Id))
            {
                // The draft's post-conditions index frames by id, and two of the same threw there —
                // after the model had answered and before the cost was recorded.
                return "The same frame id was sent twice, so a draft could not say which frame it meant.";
            }

            if (frame.OcrText?.Length > MaxOcrChars)
            {
                return "One frame carries more text than a screen could hold.";
            }

            if (frame.Image is not { Length: > 0 })
            {
                continue;
            }

            imageChars += frame.Image.Length;
            if (imageChars > MaxImageChars)
            {
                return "Those frames weigh more than a session's worth of screenshots.";
            }

            if (!Array.Exists(Images, allowed => string.Equals(allowed, frame.MediaType, StringComparison.OrdinalIgnoreCase)))
            {
                return "A frame is not an image of a kind this client sends.";
            }

            if (!IsBase64(frame.Image))
            {
                // Otherwise the provider refuses it after being paid, and the technician is told the
                // model failed at something the client got wrong.
                return "A frame's image is not valid base64.";
            }
        }

        return null;
    }

    private static string? Transcript(SummarizeBundle bundle)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in bundle.Transcript)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.Id))
            {
                return "Every transcript segment needs an id.";
            }

            if (!ids.Add(segment.Id))
            {
                return "The same transcript segment id was sent twice.";
            }

            if (segment.Text?.Length > MaxTextChars)
            {
                return "One transcript segment is longer than anybody speaks.";
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes rather than pattern-matches, so padding and length are checked too. Rented, because at
    /// twelve megabytes a copy per request is a copy worth not making.
    /// </summary>
    private static bool IsBase64(string value)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Base64.GetMaxDecodedFromUtf8Length(value.Length));
        try
        {
            return Convert.TryFromBase64String(value, buffer, out _);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Letters, digits and the punctuation an opaque id uses. No spaces, no separators, nothing that
    /// changes meaning in a path or a log line.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-.~:@+]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Identifier();
}
