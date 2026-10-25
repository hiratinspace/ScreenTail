using System.Threading.Channels;
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

    /// <summary>
    /// Segments cut from the ring and waiting for the model, in the order they were spoken.
    ///
    /// Small on purpose. Each entry holds a copy of the audio it was cut from, which is a piece of a
    /// customer's support call sitting in memory, and a deep queue would mean the model falling minutes
    /// behind the conversation rather than seconds. Four is a handful of sentences.
    ///
    /// One reader, so segments reach the assembler in the order they were said.
    ///
    /// Each carries the session it was heard in. It used to carry nothing, and the append looked the
    /// session up when the model finished, seconds later: by then the next customer's session could be
    /// recording, and it was given the last customer's words (2026-10-12 review).
    /// </summary>
    private readonly Channel<(string Session, SpeechSegment Segment, ReadOnlyMemory<short> Audio)> _pending =
        Channel.CreateBounded<(string, SpeechSegment, ReadOnlyMemory<short>)>(
            new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
    private readonly ISpeechRecogniser _recogniser;
    private readonly AppendTranscript _append;
    private readonly Func<NarrationSession?> _recording;
    private readonly Func<bool> _hearing;
    private readonly NarrationBacklog _backlog;
    private readonly NarrationOptions _options;
    private readonly VoiceActivity _voice = new();
    private readonly SpeechGate _gate;
    private readonly short[] _ring;
    private readonly short[] _frame;
    private long _written;
    private int _filled;

    /// <summary>
    /// The session the current stretch of listening belongs to, and where on its clock the stretch began.
    /// Null between stretches. Set by the first chunk heard and cleared by <see cref="Discard"/>, so the
    /// gate is never open without one.
    /// </summary>
    private NarrationSession? _stretch;

    /// <summary>One per session, on the drain's side: ids and the monotonic clamp both start again with each.</summary>
    private TranscriptAssembler _assembler = new();
    private string? _assembling;

    /// <summary>The session the detector last learned a room for, so a new one starts by listening to its own.</summary>
    private string? _heardSession;

    /// <summary>
    /// The session a sentence still being said is counted against, or null when the gate is closed.
    ///
    /// Stop moves the session on, and the recorder only finds out on the next chunk, when it cuts what
    /// was being said and queues it. Finalize waits for what the backlog says is owed, and a sentence
    /// not yet queued was owed nothing, so a Stop whose sources closed faster than one audio buffer
    /// drafted the note without the last thing said. Counted from the moment the gate opens, it is
    /// waited for like a segment with the model, and the count passes to the segment when it is cut.
    /// </summary>
    private string? _holding;

    /// <param name="recording">
    /// The session recording right now, or null when none is.
    ///
    /// Asked of every chunk as it arrives, rather than of every segment as it is written. The microphone
    /// is opened when the service starts and listens for the life of it, so without this everything said
    /// between sessions — and while capture was suppressed for a password field — went through Whisper,
    /// and whether it was stored came down to whether the segment happened to close before capture came
    /// back. A password read aloud during a pause, finished after the resume, was stored (2026-09-19
    /// review).
    ///
    /// It names the session rather than only saying yes, and says where on that session's clock this
    /// moment is.
    /// Each segment is stamped with the session it was heard in and placed on its timeline, and is
    /// written into that session or nowhere.
    ///
    /// Required. It used to default to "always", so a host that dropped the argument compiled, opened
    /// the microphone for the life of the service and transcribed everything said between sessions
    /// (2026-10-12 review). Only a recording that is all narration, a file transcribed on the command
    /// line, has no sessions to ask about, and it says so by using the shorter constructor.
    /// </param>
    /// <param name="hearing">
    /// Whether what is said right now may be kept, asked of every chunk while a session is recording.
    /// The host answers no while the window in front may not be photographed: an excluded app such as a
    /// password manager, an elevated window, anything out of scope (INV-6). The microphone stays open,
    /// because the session is still recording, and the audio is dropped where it arrives. Defaults to
    /// "always".
    /// </param>
    /// <param name="backlog">
    /// Where the segments each session is still owed are counted, shared with the session machine so
    /// finalize can wait for them and discard can drop them. The recorder keeps its own when none is given.
    /// </param>
    public NarrationRecorder(
        IMicrophone microphone,
        ISpeechRecogniser recogniser,
        AppendTranscript append,
        Func<NarrationSession?> recording,
        NarrationOptions? options = null,
        SpeechGateOptions? gate = null,
        Func<bool>? hearing = null,
        NarrationBacklog? backlog = null)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _hearing = hearing ?? (() => true);
        _backlog = backlog ?? new NarrationBacklog();
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _recogniser = recogniser ?? throw new ArgumentNullException(nameof(recogniser));
        _append = append ?? throw new ArgumentNullException(nameof(append));
        _options = options ?? new NarrationOptions();
        _gate = new SpeechGate(gate);
        _ring = new short[Math.Max(1, (int)(_options.Buffer.TotalSeconds * microphone.SampleRate))];
        _frame = new short[Math.Max(1, (int)(_options.Frame.TotalSeconds * microphone.SampleRate))];
    }

    /// <summary>
    /// A recording that is all narration, such as a file transcribed on the command line: one session
    /// for the whole of it, every word of which may be kept.
    ///
    /// Not for the service. Nothing here asks whether a session is recording or whether the window in
    /// front may be heard, so a service recorder built this way would hold the microphone open all day
    /// and keep what was said over a password manager (INV-6, INV-9). The service's recorder passes both.
    /// </summary>
    public NarrationRecorder(IMicrophone microphone, ISpeechRecogniser recogniser, AppendTranscript append)
        : this(microphone, recogniser, append, static () => new NarrationSession("narration", 0))
    {
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

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var draining = DrainAsync(stopping.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_recording() is null)
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
                await QueueAsync(last, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (_gate.Flush() is { } last && !ct.IsCancellationRequested)
            {
                await QueueAsync(last, ct).ConfigureAwait(false);
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
            LetGo();

            // Nothing more will be queued, so the drain can finish what it has. A segment already cut
            // from the ring is a thing the technician said, and dropping it because the service is
            // stopping would lose the end of the session -- usually the outcome, which is the half a
            // note most needs.
            _pending.Writer.TryComplete();
            try
            {
                await draining.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled rather than drained. The caller is shutting down.
            }
        }
    }

    /// <summary>
    /// Holds the microphone open for one session's worth of listening.
    ///
    /// Returns when the session ends, so the enumerator is disposed and the device closed with it. What
    /// was being said at that moment is cut there and kept; the gate and the buffer are emptied on the
    /// way out, so nothing heard after the boundary can be joined on to it.
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
                if (_recording() is not { } now)
                {
                    await EndStretchAsync(ct).ConfigureAwait(false);
                    return true;
                }

                if (_stretch is { } stretch && !string.Equals(stretch.SessionId, now.SessionId, StringComparison.Ordinal))
                {
                    // One session ended and the next began between two chunks. What was being said is
                    // the first session's, cut here; nothing said in one may be finished in the other.
                    await EndStretchAsync(ct).ConfigureAwait(false);
                }

                if (!_hearing())
                {
                    // Something in front that may not be captured. Dropped where it arrives, and what was
                    // being said is forgotten at the boundary rather than kept: the words before a vault
                    // came to the front are the start of the sentence that was read off it.
                    if (_stretch is not null)
                    {
                        Discard();
                    }

                    continue;
                }

                if (_stretch is null)
                {
                    if (!string.Equals(_heardSession, now.SessionId, StringComparison.Ordinal))
                    {
                        // A new session starts by listening to its own room. The floor learned in a
                        // quiet office would make the next customer's server room read as speech for
                        // half a minute, and that half minute would go to the model (2026-10-12 review).
                        _voice.Reset();
                        _heardSession = now.SessionId;
                    }

                    _stretch = now;
                }

                await OfferAsync(chunk, ct).ConfigureAwait(false);
            }

            // The microphone itself stopped. Whatever was being said is still worth keeping.
            if (_gate.Flush() is { } last)
            {
                await QueueAsync(last, ct).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            Listening = false;
        }
    }

    /// <summary>
    /// The session went away between two chunks: stopped, paused, suppressed, or replaced by the next.
    ///
    /// Whatever was being said is cut here and kept. It was heard while the session was recording, and
    /// nothing from after this moment is in it, so it is the session's as much as a frame staged just
    /// before a pause is (backlog, 2026-10-14). Until the 2026-10-12 review only the microphone running
    /// out flushed the gate; a real Stop reset it, and the last thing said in every session, usually the
    /// outcome, was dropped. The session machine accepts the segment while it pauses or finalizes and
    /// refuses it once the session is closed, so a stop that has already finished simply loses it.
    ///
    /// Capture coming back starts a new sentence, which is the honest reading: nobody can say what was
    /// said in between, and this is the mechanism whose whole job is not to guess.
    /// </summary>
    private async ValueTask EndStretchAsync(CancellationToken ct)
    {
        if (_gate.Flush() is { } last)
        {
            await QueueAsync(last, ct).ConfigureAwait(false);
        }

        Discard();
    }

    /// <summary>
    /// Forgets whatever was being said.
    ///
    /// The gate as well as the buffer. At a session boundary <see cref="EndStretchAsync"/> has already
    /// kept what was open; where the window in front may not be captured, nothing is kept.
    ///
    /// The gate's clock restarts with the buffer. It used to keep running while the buffer's count went
    /// back to zero, so after the first pause, suppression or session end every segment pointed at audio
    /// that was never written, came out empty, and narration was lost until the service restarted
    /// (2026-10-12 review). The stretch goes too: what is heard next is placed by the session's clock
    /// when it is heard.
    /// </summary>
    private void Discard()
    {
        LetGo();
        _gate.Reset();
        _filled = 0;
        _stretch = null;
        if (_written > 0)
        {
            _written = 0;
            Array.Clear(_ring);
        }
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
                await QueueAsync(segment, ct).ConfigureAwait(false);
            }

            // One comparison a frame; the backlog's lock is taken only when the gate opens or closes.
            if (_gate.Speaking != (_holding is not null))
            {
                Hold();
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

    /// <summary>
    /// Cuts a finished segment out of the ring and hands it to the transcriber.
    ///
    /// The cut happens here, on the microphone's thread, while the audio is certainly still in the ring.
    /// The model runs elsewhere. It used to run here: whisper.cpp takes between 2.6 and 10.8 seconds for
    /// a segment (ADR-0001), and for all of that the loop consuming audio was not consuming audio — so
    /// the sentence after the one being transcribed aged out of the ring and was lost, which the old
    /// comment on this method admitted in as many words (2026-09-20 review).
    /// </summary>
    private async ValueTask QueueAsync(SpeechSegment segment, CancellationToken ct)
    {
        if (_stretch is not { } stretch)
        {
            // Not reachable: the gate is only fed inside a stretch and is emptied with it. Kept so that
            // nothing can ever be queued without a session to belong to.
            return;
        }

        Heard++;
        if (!_recogniser.Ready)
        {
            MissedWaitingForModel++;
            return;
        }

        var audio = Extract(segment);
        if (audio.IsEmpty)
        {
            MissedWaitingForModel++;
            return;
        }

        // Cut on the gate's clock, which starts with the stretch; stored on the session's, which is what
        // clicks and frames are stamped with.
        var placed = segment with { StartMs = segment.StartMs + stretch.AtMs, EndMs = segment.EndMs + stretch.AtMs };

        // Owed from now until it is written or refused, so finalize waits for it. A sentence the gate has
        // finished with was already counted while it was being said, and that count becomes this one's.
        if (!_gate.Speaking && string.Equals(_holding, stretch.SessionId, StringComparison.Ordinal))
        {
            _holding = null;
        }
        else
        {
            _backlog.Owe(stretch.SessionId);
        }

        try
        {
            // Bounded, and waits rather than dropping: the queue only fills when the model is slower than
            // the technician is talkative, and an unbounded one would hold a support call in memory.
            // Waiting here costs what the old code cost, and only once the buffer is already full.
            await _pending.Writer.WriteAsync((stretch.SessionId, placed, audio), ct).ConfigureAwait(false);
        }
        catch
        {
            _backlog.Settle(stretch.SessionId);
            throw;
        }
    }

    /// <summary>The gate opened or closed: count the sentence being said against its session, or stop counting it.</summary>
    private void Hold()
    {
        if (_gate.Speaking && _stretch is { } stretch)
        {
            _backlog.Owe(stretch.SessionId);
            _holding = stretch.SessionId;
        }
        else
        {
            LetGo();
        }
    }

    /// <summary>Nothing is being said any more, or what was is no longer the session's: the count goes.</summary>
    private void LetGo()
    {
        if (_holding is { } session)
        {
            _holding = null;
            _backlog.Settle(session);
        }
    }

    /// <summary>Transcribes queued segments one at a time, in the order they were spoken.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        await foreach (var (session, segment, audio) in _pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                // A session discarded or finalized since this was queued has stopped taking narration.
                // The model's seconds are not spent on words nobody can store.
                if (_backlog.Wants(session))
                {
                    await TranscribeAsync(session, segment, audio, ct).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // One segment must not end narration; RunAsync's catch is the same rule.
            catch (Exception failure) when (failure is not OperationCanceledException)
#pragma warning restore CA1031
            {
                Failures++;
                Failed?.Invoke(failure);
            }
            finally
            {
                _backlog.Settle(session);
            }
        }
    }

    private async Task TranscribeAsync(string session, SpeechSegment segment, ReadOnlyMemory<short> audio, CancellationToken ct)
    {
        var heard = await _recogniser.TranscribeAsync(segment, audio, ct).ConfigureAwait(false);
        if (heard is null)
        {
            return;
        }

        if (!string.Equals(_assembling, session, StringComparison.Ordinal))
        {
            // A new session's transcript starts from its own beginning: ids from t-0001, and no clamp
            // against where the last session's ended.
            _assembler = new TranscriptAssembler();
            _assembling = session;
        }

        var written = _assembler.Add(heard);
        if (written is null)
        {
            Rejected++;
            return;
        }

        if (await _append(session, written, ct).ConfigureAwait(false))
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
