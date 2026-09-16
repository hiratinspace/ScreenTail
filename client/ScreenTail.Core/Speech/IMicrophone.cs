using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Speech;

/// <summary>
/// The technician's own microphone, and the only audio source in the product (INV-9).
///
/// v1 records the technician and never the person on the other end of the call. That is not a setting or
/// a default: twelve US states require all-party consent for audio, and end-user audio is gated behind
/// the consent workflow in ST-123. There is one implementation, it opens the local capture device, and
/// there is no code path to any other source — which is what makes INV-9 a property of the design rather
/// than a rule someone has to remember.
/// </summary>
public interface IMicrophone : IAsyncDisposable
{
    /// <summary>
    /// The device, or null when there is none — no hardware, or Windows has blocked access.
    ///
    /// A device name, never audio and never content, so it is safe in the diagnostics panel a technician
    /// turns round and shows a customer (INV-10).
    /// </summary>
    string? DeviceName { get; }

    /// <summary>Samples per second. Whisper wants 16 kHz mono, and the capture side resamples to it.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Yields frames of 16-bit mono samples until cancelled. Ends immediately when there is no
    /// microphone, which is a session without narration rather than an error.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync(CancellationToken ct = default);
}

/// <summary>
/// Turns a stretch of speech into words (ST-027). Local: the audio never leaves the machine, whatever the
/// tenant's egress settings say, because a transcript of a support call is the most sensitive thing a
/// session produces.
/// </summary>
public interface ISpeechRecogniser : IAsyncDisposable
{
    /// <summary>
    /// False while the model is still downloading. Audio offered before this is true is dropped and
    /// counted, never queued: the model is hundreds of megabytes, and a queue that grew for a whole
    /// session would then try to transcribe an hour of audio at once.
    /// </summary>
    bool Ready { get; }

    /// <returns>What was heard, or null when the recogniser produced nothing at all.</returns>
    Task<Transcribed?> TranscribeAsync(SpeechSegment segment, ReadOnlyMemory<short> audio, CancellationToken ct = default);
}

/// <param name="Append">
/// Writes a segment to the session. Returns false when the machine refused it, which is the ordinary
/// answer while capture is paused or suppressed (INV-6) and not a failure.
/// </param>
public delegate Task<bool> AppendTranscript(TranscriptSegment segment, CancellationToken ct);
