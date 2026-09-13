namespace ScreenTail.Core.Speech;

public sealed record SpeechGateOptions
{
    /// <summary>How much audio each voice-activity probability covers. Silero works in 32 ms frames at 16 kHz.</summary>
    public TimeSpan FrameLength { get; init; } = TimeSpan.FromMilliseconds(32);

    /// <summary>Above this, a frame is speech. Deliberately higher than <see cref="LeaveAt"/>.</summary>
    public double EnterAt { get; init; } = 0.6;

    /// <summary>
    /// Below this, speech has stopped. Lower than <see cref="EnterAt"/> on purpose: with one threshold, a
    /// probability hovering around it would open and close a segment every other frame and cut a sentence
    /// into a dozen pieces, each paying Whisper's fixed cost.
    /// </summary>
    public double LeaveAt { get; init; } = 0.35;

    /// <summary>
    /// How long the gate stays open after the probability falls. A speaker pauses between words and
    /// clauses, and closing on the first quiet frame would end a segment mid-sentence — which costs
    /// accuracy as well as time, because Whisper reads a whole segment for context.
    /// </summary>
    public TimeSpan Hangover { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Audio kept from before the gate opened. Voice activity is recognised a frame or two after speech
    /// starts, so without this every segment loses its first consonant — "restart" becomes "estart".
    /// </summary>
    public TimeSpan Preroll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Shorter than this and it was a cough, a keyboard, or a chair. Transcribing it costs Whisper's fixed
    /// per-segment cost to produce nothing, or worse, to hallucinate a word onto noise.
    /// </summary>
    public TimeSpan MinimumSpeech { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A segment is cut here even if the speaker has not stopped. Two reasons: ST-027 budgets five seconds
    /// of lag, and a segment that never ends is a segment that is never transcribed; and Whisper's cost
    /// grows with length, so an unbounded one would block the queue.
    /// </summary>
    public TimeSpan MaximumSegment { get; init; } = TimeSpan.FromSeconds(20);
}

/// <param name="StartMs">Where the segment begins in the session, including <see cref="SpeechGateOptions.Preroll"/>.</param>
/// <param name="Truncated">True when the segment was cut at the maximum rather than because speech stopped.</param>
public sealed record SpeechSegment(long StartMs, long EndMs, bool Truncated)
{
    public long DurationMs => EndMs - StartMs;
}

/// <summary>
/// Decides which parts of the microphone stream are worth transcribing (ST-027).
///
/// ADR-0001 finding 8 is why this exists: on the hosted runner, feeding Whisper every five seconds of
/// audio used most of the CPU, and on the laptop 49–62% of eight threads. A technician talks for a
/// fraction of a session — the rest is typing, reading and waiting — so gating on voice activity is the
/// difference between the speech pipeline being always busy and mostly idle, and ST-031 budgets 15% of the
/// machine for all of ScreenTail together.
///
/// The policy is here rather than beside the model because none of it is about audio: it is hysteresis, a
/// hangover, a minimum and a maximum over a stream of numbers. That makes it testable on a machine with no
/// microphone, which is the only way these thresholds get argued about before they are shipped.
/// </summary>
public sealed class SpeechGate(SpeechGateOptions? options = null)
{
    private readonly SpeechGateOptions _options = options ?? new SpeechGateOptions();
    private long _frames;
    private long? _openedAt;
    private long _lastLoudMs;

    public SpeechGateOptions Options => _options;

    /// <summary>True while the gate is open and audio is being kept.</summary>
    public bool Speaking => _openedAt is not null;

    /// <summary>Frames offered since the last <see cref="Reset"/>. Times <see cref="SpeechGateOptions.FrameLength"/> is the audio seen.</summary>
    public long FramesSeen => _frames;

    /// <summary>Frames that were inside an open segment — what the transcriber is actually asked to read.</summary>
    public long FramesKept { get; private set; }

    /// <summary>Segments dropped for being too short to be speech. Counted, because a rising number means the thresholds are wrong.</summary>
    public long TooShort { get; private set; }

    /// <summary>
    /// Offers one frame's voice-activity probability and returns a segment when one has just finished.
    ///
    /// The segment's end is where speech stopped, not where the hangover expired: the trailing silence is
    /// what proved the speech had ended, and including it would put a second of nothing on the end of
    /// every segment and into the timeline alignment ST-028 does against it.
    /// </summary>
    public SpeechSegment? Offer(double probability)
    {
        var frame = _frames++;
        var atMs = (long)(frame * _options.FrameLength.TotalMilliseconds);
        var endOfFrameMs = atMs + (long)_options.FrameLength.TotalMilliseconds;

        if (_openedAt is null)
        {
            if (probability >= _options.EnterAt)
            {
                _openedAt = Math.Max(0, atMs - (long)_options.Preroll.TotalMilliseconds);
                _lastLoudMs = endOfFrameMs;
                FramesKept++;
            }

            return null;
        }

        FramesKept++;
        if (probability > _options.LeaveAt)
        {
            _lastLoudMs = endOfFrameMs;
        }

        if (endOfFrameMs - _openedAt.Value >= _options.MaximumSegment.TotalMilliseconds)
        {
            // Cut mid-speech. The next frame opens a new segment immediately rather than waiting for
            // speech to restart, because the speaker has not stopped and the words must not be dropped.
            var cut = new SpeechSegment(_openedAt.Value, endOfFrameMs, Truncated: true);
            _openedAt = endOfFrameMs;
            _lastLoudMs = endOfFrameMs;
            return cut;
        }

        if (endOfFrameMs - _lastLoudMs < _options.Hangover.TotalMilliseconds)
        {
            return null;
        }

        var segment = new SpeechSegment(_openedAt.Value, _lastLoudMs, Truncated: false);
        _openedAt = null;

        if (segment.DurationMs < _options.MinimumSpeech.TotalMilliseconds)
        {
            TooShort++;
            return null;
        }

        return segment;
    }

    /// <summary>
    /// The stream ended — the session stopped, or the microphone went away — so anything open is finished
    /// now. Without this, whatever the technician said last would be held forever and never transcribed.
    /// </summary>
    public SpeechSegment? Flush()
    {
        if (_openedAt is not { } openedAt)
        {
            return null;
        }

        _openedAt = null;
        var segment = new SpeechSegment(openedAt, _lastLoudMs, Truncated: false);
        if (segment.DurationMs < _options.MinimumSpeech.TotalMilliseconds)
        {
            TooShort++;
            return null;
        }

        return segment;
    }

    public void Reset()
    {
        _frames = 0;
        _openedAt = null;
        _lastLoudMs = 0;
        FramesKept = 0;
        TooShort = 0;
    }
}
