using System.Globalization;
using System.Text.RegularExpressions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Speech;

/// <param name="Text">What the model returned for this segment, already trimmed.</param>
/// <param name="Confidence">The model's own confidence, 0–1.</param>
public sealed record Transcribed(SpeechSegment Segment, string Text, double Confidence);

/// <summary>
/// Turns what the model heard into transcript segments the rest of the product can trust (ST-027).
///
/// Two rules that are not bookkeeping:
///
/// <b>The speaker is always the technician (INV-9).</b> v1 captures one microphone and the schema reserves
/// <c>end_user</c> for the v1.2 consent workflow. Nothing here can produce it, so a future change has to
/// go through the consent ticket rather than through a field that happened to be settable.
///
/// <b>Silence must not become speech.</b> Whisper emits confident, fluent text for audio that contains
/// none — "Thank you.", "Thanks for watching!", a line of subtitle credits — and it does it most on the
/// quiet stretches a support call is mostly made of. That is ordinarily a nuisance; here it is a
/// correctness problem with teeth, because ST-061 verifies a drafted quotation by checking it against the
/// transcript. A hallucinated line does not just add noise: it makes a fabricated quote <em>verifiable</em>,
/// and it would be published into a customer's ticket as something the technician said.
/// </summary>
public sealed class TranscriptAssembler(TranscriptOptions? options = null)
{
    private readonly TranscriptOptions _options = options ?? new TranscriptOptions();
    private long _lastEndMs = -1;
    private int _next;

    /// <summary>How many segments were dropped as hallucinated, for the diagnostics panel.</summary>
    public int Rejected { get; private set; }

    public int Kept { get; private set; }

    /// <summary>
    /// The segment as it should be stored, or null when it should not be stored at all.
    ///
    /// Null is not an error and is not rare: most of a session is silence, and the gate lets through
    /// stretches that turn out to hold no speech.
    /// </summary>
    public TranscriptSegment? Add(Transcribed heard)
    {
        ArgumentNullException.ThrowIfNull(heard);

        var text = Collapse(heard.Text);
        if (text.Length == 0 || IsHallucination(text, heard))
        {
            Rejected++;
            return null;
        }

        // Monotonic, because ts_ms orders everything in the session and the drafting prompt reads the
        // transcript in order. Segments are transcribed on a queue and can finish out of order; clamping
        // here keeps the stored order the order they were spoken in.
        var startMs = Math.Max(heard.Segment.StartMs, _lastEndMs + 1);
        var endMs = Math.Max(startMs, heard.Segment.EndMs);
        _lastEndMs = endMs;
        Kept++;

        return new TranscriptSegment
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"t-{++_next:D4}"),
            TsMs = startMs,
            EndMs = endMs,

            // INV-9, and not a parameter. There is no code path to any other speaker in v1.
            Speaker = Speaker.Tech,
            Text = text,
            Confidence = Math.Clamp(heard.Confidence, 0, 1),
        };
    }

    /// <summary>
    /// Whether this looks like the model talking to itself rather than reporting speech.
    ///
    /// Three signals, and it takes more than one to drop a segment where the text is plausible. A
    /// technician really might say "thank you", and throwing that away silently is its own failure — so a
    /// known phrase is only rejected when the audio or the confidence agrees it was not spoken.
    /// </summary>
    private bool IsHallucination(string text, Transcribed heard)
    {
        var known = Boilerplate.IsMatch(text);

        // A rate no one speaks at. Whisper's inventions are fluent sentences attached to a second of
        // silence, so the giveaway is characters per second rather than the words themselves.
        var seconds = Math.Max(heard.Segment.DurationMs, 1) / 1000.0;
        var impossiblyFast = text.Length / seconds > _options.MaximumCharactersPerSecond;

        var unsure = heard.Confidence < _options.MinimumConfidence;

        if (impossiblyFast)
        {
            return true;
        }

        // A bare known phrase on its own, from a short segment, with nothing else in it. "Thank you" after
        // four seconds of talking is a technician being polite; the same words alone against a second of
        // room tone are the model filling a gap.
        if (known && (unsure || heard.Segment.DurationMs < _options.ShortSegmentMs))
        {
            return true;
        }

        return unsure && text.Length < _options.MinimumUnsureLength;
    }

    /// <summary>Whitespace runs become single spaces, so a comparison against the text is about words.</summary>
    private static string Collapse(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Whitespace.Replace(text.Trim(), " ");

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// What Whisper says when it has nothing to say. Drawn from its training data — subtitle tracks — which
    /// is why the phrases are the ones a video ends with.
    /// </summary>
    private static readonly Regex Boilerplate = new(
        @"^(?:"
        + @"thank(?:s| you)(?: (?:very|so) much)?(?: for watching)?"
        + @"|thanks for watching(?:!|\.)?"
        + @"|please subscribe(?:.*)?"
        + @"|subtitles? by.*"
        + @"|(?:amara\.org|www\..*)"
        + @"|\[?(?:music|applause|silence|blank_audio|inaudible)\]?"
        + @"|[♪♫♩ \.\-_]+"
        + @")[\s.!,]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}

public sealed record TranscriptOptions
{
    /// <summary>
    /// Above this, nobody said it. Ordinary speech runs about 15 characters a second; twice that is a
    /// sentence attached to audio far too short to contain it.
    /// </summary>
    public double MaximumCharactersPerSecond { get; init; } = 32;

    /// <summary>Below this the model is guessing, which matters only alongside another signal.</summary>
    public double MinimumConfidence { get; init; } = 0.5;

    /// <summary>Short enough that a whole polite sentence in it is suspicious.</summary>
    public long ShortSegmentMs { get; init; } = 1500;

    /// <summary>A low-confidence fragment this short carries no meaning worth the risk of inventing one.</summary>
    public int MinimumUnsureLength { get; init; } = 12;
}
