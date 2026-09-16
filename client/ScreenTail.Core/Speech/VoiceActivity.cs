namespace ScreenTail.Core.Speech;

/// <summary>
/// How likely it is that a frame of microphone audio contains someone talking (ST-027).
///
/// <see cref="SpeechGate"/> has always taken a probability per frame and nothing produced one. This is
/// that half, and what it decides is how much of a support call reaches a transcriber at all — too
/// generous and every fan and keystroke is sent to Whisper, which answers non-speech with fluent invented
/// sentences; too mean and the technician's explanation of what they just did never arrives.
///
/// <b>It measures signal against the room, not loudness.</b> A fixed threshold works on the machine it
/// was tuned on: a technician beside a server rack and a technician in a quiet office have noise floors
/// tens of decibels apart, and one number either transcribes the first one's silence or hears nothing
/// from the second. So the floor is learned, and what counts is how far above it a frame sits.
///
/// <b>The floor is learned only while nobody is talking.</b> Tracking every frame would drag it up behind
/// a speaker, and the back half of a long sentence would fall below the threshold — a technician
/// explaining a fix, transcribed for three seconds and then cut off mid-clause.
/// </summary>
public sealed class VoiceActivity(VoiceActivityOptions? options = null)
{
    private readonly VoiceActivityOptions _options = options ?? new VoiceActivityOptions();
    private double? _floorDb;
    private int _speakingFrames;

    /// <summary>The quietest level a frame is allowed to report, and what digital silence reads as.</summary>
    private const double Silence = -100;

    /// <summary>What the room is currently believed to sound like, in dBFS. Diagnostics only.</summary>
    public double NoiseFloorDb => _floorDb ?? Silence;

    /// <summary>The last frame's level, in dBFS. Diagnostics only; never audio, never content.</summary>
    public double LevelDb { get; private set; } = Silence;

    /// <summary>
    /// Offers one frame and returns a probability between 0 and 1.
    /// </summary>
    /// <param name="frame">16-bit mono samples. An empty frame is not speech.</param>
    public double Offer(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty)
        {
            return 0;
        }

        LevelDb = LevelOf(frame);

        // The first frame *is* the room. Starting the floor at silence instead made every real signal
        // look like speech, which meant the floor was never allowed to learn and never came off silence:
        // a machine beside a fan would have transcribed the fan all day.
        _floorDb ??= LevelDb;
        var snr = LevelDb - _floorDb.Value;

        // Straight line between "indistinguishable from the room" and "unmistakably someone talking".
        // A ramp rather than a threshold, because the gate's own hysteresis is what turns this into a
        // decision, and handing it a hard 0 or 1 would leave that band with nothing to work on.
        var probability = Math.Clamp(
            (snr - _options.QuietDb) / Math.Max(_options.LoudDb - _options.QuietDb, 1e-6),
            0,
            1);

        Learn(LevelDb, probability);
        return probability;
    }

    /// <summary>Forgets the room. A new session on a machine that has moved starts by listening again.</summary>
    public void Reset()
    {
        _floorDb = null;
        LevelDb = Silence;
        _speakingFrames = 0;
    }

    /// <summary>Root-mean-square level in dBFS, floored so that silence has a number rather than an infinity.</summary>
    private static double LevelOf(ReadOnlySpan<short> frame)
    {
        double sum = 0;
        foreach (var sample in frame)
        {
            var value = sample / 32768.0;
            sum += value * value;
        }

        var rms = Math.Sqrt(sum / frame.Length);
        return rms <= 0 ? Silence : Math.Max(Silence, 20 * Math.Log10(rms));
    }

    /// <summary>
    /// Updates the room's level.
    ///
    /// Down at once, up at a crawl, and normally not at all while someone seems to be talking. A quieter
    /// frame is always evidence about the room — the quietest recent moment is what a room sounds like —
    /// so it is believed immediately even mid-sentence. A louder one might be the start of a word, so it
    /// is followed slowly and only during silence.
    ///
    /// The exception is a run of "speech" that has gone on far longer than anyone speaks. That is not a
    /// sentence, it is a machine that started making a noise, and refusing to learn from it would leave
    /// the floor permanently below a sound that never stops — every frame forever sent to a transcriber
    /// that answers non-speech with invented sentences.
    /// </summary>
    private void Learn(double levelDb, double probability)
    {
        var speaking = probability > _options.LearnBelow;
        _speakingFrames = speaking ? _speakingFrames + 1 : 0;

        if (levelDb < _floorDb)
        {
            _floorDb = levelDb;
            return;
        }

        if (speaking && _speakingFrames < _options.RelearnAfterFrames)
        {
            return;
        }

        _floorDb = Math.Min(levelDb, _floorDb!.Value + _options.RiseDbPerFrame);
    }
}

/// <param name="QuietDb">Below this much above the room, a frame is not speech at all.</param>
/// <param name="LoudDb">At this much above the room, a frame is certainly speech.</param>
public sealed record VoiceActivityOptions
{
    /// <summary>
    /// Conversational speech sits 15-30 dB over a room. Six is low enough to catch the end of a sentence
    /// as a technician trails off, which is usually the half that says what the outcome was.
    /// </summary>
    public double QuietDb { get; init; } = 6;

    public double LoudDb { get; init; } = 18;

    /// <summary>
    /// Above this probability the frame is treated as speech and the room is not updated from it.
    /// Deliberately well under the gate's own enter threshold: a frame that might be speech should not
    /// teach the floor, even if it is not certain enough to open the gate.
    /// </summary>
    public double LearnBelow { get; init; } = 0.2;

    /// <summary>
    /// How fast the floor may climb, per frame. At the gate's 32 ms frames this is about 1.5 dB a second,
    /// so a room that genuinely gets louder is learned in a few seconds and a pause between words is not.
    /// </summary>
    public double RiseDbPerFrame { get; init; } = 0.05;

    /// <summary>
    /// How many consecutive speech-looking frames before the room is relearned anyway. At 32 ms frames
    /// this is thirty seconds — longer than the gate's own twenty-second ceiling on a single segment, so
    /// it never interrupts a person, and short enough that a machine which starts humming is learned
    /// within a minute rather than never.
    /// </summary>
    public int RelearnAfterFrames { get; init; } = 940;
}
