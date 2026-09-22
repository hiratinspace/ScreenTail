using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Speech;

/// <param name="Frame">
/// How much audio is judged at once. The gate's own frame length, because the two have to agree: a
/// detector working on a different window than the gate would open segments at boundaries the gate never
/// saw.
/// </param>
public sealed record NarrationOptions
{
    public TimeSpan Frame { get; init; } = new SpeechGateOptions().FrameLength;

    /// <summary>
    /// How much audio is kept available for a segment to be cut out of. The gate's longest segment plus
    /// its pre-roll, plus a little: enough to serve any segment the gate can produce, and not a byte
    /// more, because this is a customer's support call sitting in memory.
    /// </summary>
    public TimeSpan Buffer { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long to wait between looking for a session when none is running.
    ///
    /// A fallback, not the mechanism: the host nudges the recorder when the capture state changes, and
    /// this only decides how long a missed nudge costs. Half a second is short enough that the worst
    /// case is the opening word of a session and long enough that an idle service is doing nothing.
    /// </summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Microphone in, transcript segments out (ST-027).
///
/// The pieces either side of this have existed and been tested since the speech pipeline was started:
/// <see cref="SpeechGate"/> decides where speech begins and ends, <see cref="TranscriptAssembler"/>
/// decides whether what came back is something a person said or something the model invented. Nothing
/// joined them, so a session recorded clicks and screenshots and heard nothing at all.
///
/// The order matters. Audio is judged frame by frame, the gate turns runs of speech into segments, and
/// only those stretches are transcribed. Sending everything would be simpler and much worse: Whisper's
/// failure mode on silence is not silence, it is a fluent sentence nobody said, and those sentences would
/// end up in a customer's ticket note.
///
/// <b>Audio is never stored.</b> It lives in a ring buffer long enough to cut the current segment out of
/// and is overwritten continuously. The session keeps the words, never the recording — there is no
/// retention rule to get right because there is nothing to retain.
/// </summary>
public sealed class NarrationRecorder : IDisposable
{
    private readonly IMicrophone _microphone;

    /// <summary>Raised when a session starts or ends, so the loop need not poll to find out.</summary>
    private readonly SemaphoreSlim _wanted = new(0);
    private readonly ISpeechRecogniser _recogniser;
    private readonly AppendTranscript _append;
    private readonly Func<bool> _recording;
    private readonly NarrationOptions _options;
    private readonly VoiceActivity _voice = new();
    private readonly SpeechGate _gate;
    private readonly TranscriptAssembler _assembler = new();
    private readonly short[] _ring;
    private readonly short[] _frame;
    private long _written;
    private int _filled;

    /// <param name="recording">
    /// Whether the session is recording right now.
    ///
    /// Asked of every chunk as it arrives, rather than of every segment as it is written. The microphone
    /// is opened when the service starts and listens for the life of it, so without this everything said
    /// between sessions — and while capture was suppressed for a password field — went through Whisper,
    /// and whether it was stored came down to whether the segment happened to close before capture came
    /// back. A password read aloud during a pause, finished after the resume, was stored (2026-09-19
    /// review).
    ///
    /// It defaults to "always", which is what the tests of the pipeline itself want and what nothing
    /// that ships should use.
    /// </param>
    public NarrationRecorder(
        IMicrophone microphone,
        ISpeechRecogniser recogniser,
        AppendTranscript append,
        Func<bool>? recording = null,
        NarrationOptions? options = null,
        SpeechGateOptions? gate = null)
    {
        _recording = recording ?? (() => true);
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _recogniser = recogniser ?? throw new ArgumentNullException(nameof(recogniser));
        _append = append ?? throw new ArgumentNullException(nameof(append));
        _options = options ?? new NarrationOptions();
        _gate = new SpeechGate(gate);
        _ring = new short[Math.Max(1, (int)(_options.Buffer.TotalSeconds * microphone.SampleRate))];
        _frame = new short[Math.Max(1, (int)(_options.Frame.TotalSeconds * microphone.SampleRate))];
    }

    /// <summary>The device in use, or null when there is none. A name, never audio (INV-10).</summary>
    public string? Microphone => _microphone.DeviceName;

    /// <summary>Whether audio is arriving. False on a machine with no microphone, which is not an error.</summary>
    public bool Listening { get; private set; }

    /// <summary>Stretches of speech the gate found.</summary>
    public long Heard { get; private set; }

    /// <summary>Segments the assembler refused as invented. Counts only, never the text (INV-10).</summary>
    public long Rejected { get; private set; }

    /// <summary>Speech that arrived before the model did, and was dropped rather than queued.</summary>
    public long MissedWaitingForModel { get; private set; }

    /// <summary>Segments written to the session.</summary>
    public long Kept { get; private set; }

    /// <summary>How many times listening ended in an exception. A number the diagnostics panel can show.</summary>
    public long Failures { get; private set; }

    /// <summary>Told when listening failed, so the host can log it. The type only, never the message.</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// A session started or ended, so there is a reason to look again.
    ///
    /// Called from the host when the capture state changes. It only wakes the loop: deciding what to do
    /// about it is <see cref="RunAsync"/>'s job, and opening an audio device has no business happening
    /// on whatever thread announced a transition.
    /// </summary>
    public void Nudge()
    {
        if (_wanted.CurrentCount == 0)
        {
            _ = _wanted.Release();
        }
    }

    /// <summary>Listens until cancelled or until the microphone stops. Returns without throwing on either.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_microphone.DeviceName is null)
        {
            // AC2. Losing narration costs a better note; stopping here would cost the session.
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_recording())
                {
                    // Nothing to hear, so the device is not held open.
                    //
                    // It used to be opened for the life of the service and the audio thrown away when no
                    // session was running. That kept INV-9, and it left Windows' microphone-in-use
                    // indicator lit all day on a machine that records for a fraction of it — which is
                    // what a customer sees when the technician turns their screen round (2026-09-20
                    // review).
                    //
                    // The wait has a timeout as well as a nudge: a missed signal should cost a moment,
                    // not the session's narration.
                    _ = await _wanted.WaitAsync(_options.Idle, ct).ConfigureAwait(false);
                    continue;
                }

                if (!await ListenWhileRecordingAsync(ct).ConfigureAwait(false))
                {
                    // The microphone itself stopped -- unplugged, or revoked. That ended narration for
                    // the life of the service before this change and still does; retrying a device that
                    // has gone is a loop, not a recovery.
                    return;
                }
            }

            // A session stopped mid-sentence still keeps what was being said. It is usually the outcome,
            // which is the half of a session a note most needs.
            if (_gate.Flush() is { } last)
            {
                await TranscribeAsync(last, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (_gate.Flush() is { } last && !ct.IsCancellationRequested)
            {
                await TranscribeAsync(last, ct).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Losing narration must not be a reason to lose the session.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            // AC2 again, for the failures that are not the microphone's. Until 2026-09-20 a store write
            // that threw ended this loop for the life of the service, and the one that threw was
            // ordinary: transcript ids restart at t-0001 on every service start, so the first thing said
            // after a restart collided with an earlier session's row. Narration stopped silently and
            // every later session was recorded without a word (2026-09-19 review).
            //
            // The type and a count, never the message: a store error can quote what it was asked to
            // write, and what it was asked to write is what a technician said (INV-10).
            Failures++;
            Failed?.Invoke(failure);
        }
        finally
        {
            Listening = false;
        }
    }

    /// <summary>
    /// Holds the microphone open for one session's worth of listening.
    ///
    /// Returns when the session ends, so the enumerator is disposed and the device closed with it. The
    /// gate is emptied on the way out for the same reason a pause empties it: a sentence begun while
    /// recording must not be finished after it.
    /// </summary>
    /// <returns>
    /// True when the session ended and there may be another; false when the microphone itself stopped,
    /// which is the end of narration rather than the end of a session.
    /// </returns>
    private async Task<bool> ListenWhileRecordingAsync(CancellationToken ct)
    {
        Listening = true;
        try
        {
            await foreach (var chunk in _microphone.ListenAsync(ct).ConfigureAwait(false))
            {
                if (!_recording())
                {
                    Discard();
                    return true;
                }

                await OfferAsync(chunk, ct).ConfigureAwait(false);
            }

            // The microphone itself stopped. Whatever was being said is still worth keeping.
            if (_gate.Flush() is { } last)
            {
                await TranscribeAsync(last, ct).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            Listening = false;
        }
    }

    /// <summary>
    /// Forgets whatever was being said.
    ///
    /// The gate as well as the buffer: a sentence that began while recording must not be finished after
    /// a pause. Capture coming back starts a new sentence, which is the honest reading — nobody can say
    /// what was said in between, and this is the mechanism whose whole job is not to guess.
    /// </summary>
    private void Discard()
    {
        _ = _gate.Flush();
        _filled = 0;
        _written = 0;
        Array.Clear(_ring);
    }

    /// <summary>Accepts however much audio arrived and judges it a frame at a time.</summary>
    private async Task OfferAsync(ReadOnlyMemory<short> chunk, CancellationToken ct)
    {
        // Walked by index rather than by re-slicing a span, because a span cannot live across the await
        // below and transcription has to happen the moment the gate closes a segment.
        var offset = 0;
        while (offset < chunk.Length)
        {
            var take = Math.Min(_frame.Length - _filled, chunk.Length - offset);
            Accept(chunk.Slice(offset, take));
            _filled += take;
            offset += take;

            if (_filled < _frame.Length)
            {
                return;
            }

            _filled = 0;
            if (_gate.Offer(_voice.Offer(_frame)) is { } segment)
            {
                await TranscribeAsync(segment, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Copies one piece into the frame being judged and into the ring it may be cut from.</summary>
    private void Accept(ReadOnlyMemory<short> part)
    {
        var samples = part.Span;
        samples.CopyTo(_frame.AsSpan(_filled));
        Append(samples);
    }

    private async Task TranscribeAsync(SpeechSegment segment, CancellationToken ct)
    {
        Heard++;
        if (!_recogniser.Ready)
        {
            MissedWaitingForModel++;
            return;
        }

        var audio = Extract(segment);
        if (audio.IsEmpty)
        {
            // The segment aged out of the ring while something else was being transcribed. Counted as a
            // miss rather than sent as whatever happens to be in the buffer now.
            MissedWaitingForModel++;
            return;
        }

        var heard = await _recogniser.TranscribeAsync(segment, audio, ct).ConfigureAwait(false);
        if (heard is null)
        {
            return;
        }

        var written = _assembler.Add(heard);
        if (written is null)
        {
            Rejected++;
            return;
        }

        if (await _append(written, ct).ConfigureAwait(false))
        {
            Kept++;
        }
    }

    private void Append(ReadOnlySpan<short> samples)
    {
        foreach (var sample in samples)
        {
            _ring[(int)(_written++ % _ring.Length)] = sample;
        }
    }

    /// <summary>
    /// Copies one segment out of the ring, or nothing when it has already been overwritten.
    ///
    /// The buffer is sized for the longest segment the gate can produce, so this only comes up empty if
    /// transcription has fallen far enough behind that the audio is gone — which is a gap worth counting,
    /// not a reason to transcribe whatever is in the buffer now.
    /// </summary>
    private ReadOnlyMemory<short> Extract(SpeechSegment segment)
    {
        var rate = _microphone.SampleRate;
        var from = segment.StartMs * rate / 1000;
        var to = Math.Min(segment.EndMs * rate / 1000, _written);
        var oldest = Math.Max(0, _written - _ring.Length);
        if (to <= from || from < oldest)
        {
            return ReadOnlyMemory<short>.Empty;
        }

        var audio = new short[to - from];
        for (long i = 0; i < audio.Length; i++)
        {
            audio[i] = _ring[(int)((from + i) % _ring.Length)];
        }

        return audio;
    }

    /// <summary>Releases the signal the loop waits on. The microphone belongs to whoever built it.</summary>
    public void Dispose() => _wanted.Dispose();
}
