using System.Runtime.CompilerServices;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-027's middle: microphone in, transcript segments out.
///
/// The gate, the assembler and the model download all existed and were tested, and nothing connected
/// them — there was no audio source, no transcriber and no loop, so a session recorded clicks and
/// screenshots and heard nothing. This is that loop, and it is in Core so the decisions can be argued
/// about without a microphone, a Windows machine or a model file.
///
/// INV-9 runs through all of it: only the technician's own microphone is ever read, and the assembler
/// has no code path to any speaker but <see cref="Speaker.Tech"/>.
/// </summary>
public sealed class NarrationRecorderTests
{
    private const int Rate = 16_000;

    [Fact]
    public async Task SomethingSaidBecomesATranscriptSegment()
    {
        var kept = new List<TranscriptSegment>();
        var microphone = new FakeMicrophone(Silence(20), Speech(40), Silence(40));
        var recorder = Recorder(microphone, new FakeRecogniser("restarted the print spooler"), kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        var segment = Assert.Single(kept);
        Assert.Equal("restarted the print spooler", segment.Text);
        Assert.Equal(Speaker.Tech, segment.Speaker);
        Assert.True(segment.EndMs > segment.TsMs);
    }

    [Fact]
    public async Task SilenceIsNeverSentToTheTranscriber()
    {
        // The whole reason the gate exists. Whisper answers non-speech with fluent invented sentences, so
        // a loop that transcribed everything would fill a ticket note with things nobody said.
        var kept = new List<TranscriptSegment>();
        var recogniser = new FakeRecogniser("this was never said");
        var recorder = Recorder(new FakeMicrophone(Silence(200)), recogniser, kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(kept);
        Assert.Equal(0, recogniser.Asked);
    }

    [Fact]
    public async Task NoMicrophoneIsNotAnError()
    {
        // ST-027 AC2. A machine with no microphone, or one Windows has blocked, still records clicks and
        // screenshots: losing narration costs a better note, and stopping the session costs the session.
        var kept = new List<TranscriptSegment>();
        var recorder = Recorder(FakeMicrophone.None, new FakeRecogniser("x"), kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(recorder.Microphone);
        Assert.False(recorder.Listening);
        Assert.Empty(kept);
    }

    [Fact]
    public async Task AudioHeardBeforeTheModelArrivesIsDroppedRatherThanQueued()
    {
        // ST-027 AC3. The model is hundreds of megabytes and downloads on first use. Holding every
        // segment until it lands would grow without bound through a whole session and then transcribe an
        // hour of audio at once; the honest outcome is a gap and a number saying how big it was.
        var kept = new List<TranscriptSegment>();
        var recogniser = new FakeRecogniser("late") { Ready = false };
        var recorder = Recorder(new FakeMicrophone(Silence(20), Speech(40), Silence(40)), recogniser, kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(kept);
        Assert.Equal(0, recogniser.Asked);
        Assert.Equal(1, recorder.MissedWaitingForModel);
    }

    [Fact]
    public async Task AnInventedSentenceIsRejectedRatherThanStored()
    {
        // The assembler's job, exercised through the loop: a segment the transcriber answered with
        // something impossibly fast for its length does not reach the session.
        var kept = new List<TranscriptSegment>();
        var recogniser = new FakeRecogniser(new string('a', 4000));
        var recorder = Recorder(new FakeMicrophone(Silence(20), Speech(40), Silence(40)), recogniser, kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(kept);
        Assert.Equal(1, recorder.Rejected);
    }

    [Fact]
    public async Task TheTranscriberIsGivenTheAudioTheGateChose()
    {
        // Including the pre-roll, which is what stops a segment beginning halfway through the first word.
        var kept = new List<TranscriptSegment>();
        var recogniser = new FakeRecogniser("hello");
        var recorder = Recorder(new FakeMicrophone(Silence(20), Speech(40), Silence(40)), recogniser, kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        var heard = Assert.Single(recogniser.Handed);
        var expected = (heard.Segment.EndMs - heard.Segment.StartMs) * Rate / 1000;
        Assert.InRange(heard.Samples, expected - Rate / 20, expected + Rate / 20);
    }

    [Fact]
    public async Task ASegmentStillOpenWhenTheSessionEndsIsNotLost()
    {
        // A technician who stops the session mid-sentence. The gate flushes, and the last thing they said
        // is usually the outcome — the half of the session a note most needs.
        var kept = new List<TranscriptSegment>();
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(60)),
            new FakeRecogniser("and that fixed it"),
            kept);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("and that fixed it", Assert.Single(kept).Text);
    }

    [Fact]
    public async Task NothingSaidWhileCaptureIsOffIsEverTranscribed()
    {
        // 2026-09-19 review. The microphone is opened when the service starts and listens for the life
        // of it, so everything a technician says between sessions — and while paused for a password
        // field — went through Whisper, and whether it was stored came down to whether the segment
        // happened to close before capture came back.
        //
        // Nothing is even transcribed now. Audio heard while capture is off is dropped where it arrives,
        // so there is no window in which a password read aloud during a pause becomes a segment looking
        // for a home (INV-6, INV-9).
        var recogniser = new FakeRecogniser("something said while paused");
        var kept = new List<TranscriptSegment>();
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(40), Silence(40)),
            recogniser,
            kept,
            recording: () => false);

        // Driven by a token rather than by the audio running out. The recorder waits for a session now
        // instead of returning when the microphone has nothing left, because in a service that is what
        // "between sessions" is (2026-09-21).
        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await Task.Delay(150);
        await stop.CancelAsync();
        await running;

        Assert.Empty(kept);
        Assert.Equal(0, recogniser.Asked);
    }

    [Fact]
    public async Task ASentenceStartedBeforeAPauseIsNotFinishedAfterIt()
    {
        // The half that made it a leak rather than waste: a segment opened while recording, the
        // technician paused mid-sentence over a password field, and the segment closed after the resume
        // and was appended. The gate is emptied at the boundary, so what was being said is dropped with
        // the rest rather than waiting to be finished.
        var recogniser = new FakeRecogniser("the password is");
        var kept = new List<TranscriptSegment>();
        var recording = true;
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(40)),
            recogniser,
            kept,
            recording: () =>
            {
                // Recording for the silence, paused from the first frame of speech onwards.
                var was = recording;
                recording = false;
                return was;
            });

        using var stop = new CancellationTokenSource();
        var running = recorder.RunAsync(stop.Token);
        await Task.Delay(150);
        await stop.CancelAsync();
        await running;

        Assert.Empty(kept);
    }

    [Fact]
    public async Task TheMicrophoneIsNotOpenedWhileNothingIsBeingRecorded()
    {
        // 2026-09-20 efficiency review, and the cost that is not measured in cycles: the device was
        // opened for the life of the service, so Windows lit the microphone-in-use indicator all day on
        // a machine that records for a fraction of it. A technician turning their screen round to a
        // customer was showing them a lit microphone.
        //
        // The audio was already dropped when no session was running (INV-9). What was missing was not
        // asking for it.
        var microphone = new FakeMicrophone(Silence(20), Speech(40));
        var recorder = Recorder(microphone, new FakeRecogniser("x"), [], recording: () => false);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await Task.Delay(150);
        await stop.CancelAsync();
        await running;

        Assert.Equal(0, microphone.Opens);
        Assert.False(recorder.Listening);
    }

    [Fact]
    public async Task TheMicrophoneOpensWhenASessionStarts()
    {
        // The other half: closing it must not be the same as never opening it.
        var microphone = new FakeMicrophone(Silence(20), Speech(40), Silence(40));
        var kept = new List<TranscriptSegment>();
        var recording = false;
        var recorder = Recorder(microphone, new FakeRecogniser("restarting the spooler"), kept, recording: () => recording);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await Task.Delay(100);
        Assert.Equal(0, microphone.Opens);

        recording = true;
        recorder.Nudge();
        for (var i = 0; i < 100 && kept.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        await stop.CancelAsync();
        await running;

        Assert.Equal(1, microphone.Opens);
        Assert.Equal("restarting the spooler", Assert.Single(kept).Text);
    }

    [Fact]
    public async Task WhatIsSaidDuringASessionIsStillKept()
    {
        // The control. A recorder that drops everything passes both tests above.
        var kept = new List<TranscriptSegment>();
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(40), Silence(40)),
            new FakeRecogniser("restarting the spooler"),
            kept,
            recording: () => true);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("restarting the spooler", Assert.Single(kept).Text);
    }

    [Fact]
    public async Task AStoreThatRefusesASegmentDoesNotEndNarration()
    {
        // The loop caught cancellation and nothing else, so one failed write stopped narration for the
        // life of the service — and the failure that actually happened was an id collision on the first
        // segment after a restart, which is to say: every session after a restart.
        var kept = new List<TranscriptSegment>();
        var refusals = 0;
        var recorder = new NarrationRecorder(
            new FakeMicrophone(Silence(20), Speech(40), Silence(40)),
            new FakeRecogniser("restarting the spooler"),
            (segment, _) =>
            {
                refusals++;
                return refusals == 1
                    ? throw new InvalidOperationException("UNIQUE constraint failed: transcript.id")
                    : Task.FromResult(Add(kept, segment));
            });

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, recorder.Failures);
    }

    [Fact]
    public async Task TheMicrophoneKeepsBeingReadWhileAModelIsStillThinking()
    {
        // 2026-09-20 efficiency review. Transcription was awaited inside the loop that consumes audio,
        // and whisper.cpp takes between 2.6 and 10.8 seconds for a segment (ADR-0001). So the microphone
        // was not read for as long as the model was busy, and the recorder's own comment admitted the
        // consequence: "the segment aged out of the ring while something else was being transcribed".
        // A technician who kept talking lost the sentence after the one being transcribed.
        var microphone = new FakeMicrophone(Silence(20), Speech(40), Silence(40));
        var recogniser = new FakeRecogniser("restarting the spooler") { Blocked = new TaskCompletionSource() };
        var kept = new List<TranscriptSegment>();
        var recorder = Recorder(microphone, recogniser, kept, recording: () => true);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);

        // Every frame read while the model has answered nothing at all.
        for (var i = 0; i < 200 && microphone.Yielded < 100; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(100, microphone.Yielded);

        recogniser.Blocked.SetResult();
        await running;

        Assert.Equal("restarting the spooler", Assert.Single(kept).Text);
    }

    private static bool Add(List<TranscriptSegment> kept, TranscriptSegment segment)
    {
        kept.Add(segment);
        return true;
    }

    private static NarrationRecorder Recorder(
        FakeMicrophone microphone,
        FakeRecogniser recogniser,
        List<TranscriptSegment> kept,
        Func<bool>? recording = null) =>
        new(
            microphone,
            recogniser,
            (segment, _) =>
            {
                kept.Add(segment);
                return Task.FromResult(true);
            },
            recording ?? (() => true));

    /// <summary>Frames of digital silence, at the gate's frame length.</summary>
    private static short[][] Silence(int frames) => Frames(frames, _ => 0);

    /// <summary>Frames of a 200 Hz tone well above the floor: what a voice looks like to the detector.</summary>
    private static short[][] Speech(int frames)
    {
        var n = 0;
        return Frames(frames, i =>
        {
            var sample = (short)(9000 * Math.Sin(2 * Math.PI * 200 * n / Rate));
            n++;
            return sample;
        });
    }

    private static short[][] Frames(int count, Func<int, short> sample)
    {
        var length = Rate * 32 / 1000;
        var frames = new short[count][];
        for (var f = 0; f < count; f++)
        {
            frames[f] = new short[length];
            for (var i = 0; i < length; i++)
            {
                frames[f][i] = sample(i);
            }
        }

        return frames;
    }

    private sealed class FakeMicrophone(params short[][][] runs) : IMicrophone
    {
        private readonly List<short[]> _frames = [.. runs.SelectMany(run => run)];

        /// <summary>
        /// How many times the device was actually opened.
        ///
        /// Windows lights the microphone-in-use indicator for as long as it is, so this is not a
        /// performance counter: it is what a customer sees over the technician's shoulder.
        /// </summary>
        public int Opens { get; private set; }

        /// <summary>How many frames the recorder has taken. The microphone's own progress, not the model's.</summary>
        public int Yielded { get; private set; }

        public static FakeMicrophone None { get; } = new() { Present = false };

        public bool Present { get; private init; } = true;

        public string? DeviceName => Present ? "Fake Microphone" : null;

        public int SampleRate => Rate;

        public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!Present)
            {
                yield break;
            }

            Opens++;
            foreach (var frame in _frames)
            {
                ct.ThrowIfCancellationRequested();
                Yielded++;
                yield return frame;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRecogniser(string text) : ISpeechRecogniser
    {
        /// <summary>Held until a test lets go, standing in for a model that takes seconds to answer.</summary>
        public TaskCompletionSource? Blocked { get; set; }

        public bool Ready { get; set; } = true;

        public int Asked { get; private set; }

        public List<(SpeechSegment Segment, int Samples)> Handed { get; } = [];

        public async Task<Transcribed?> TranscribeAsync(SpeechSegment segment, ReadOnlyMemory<short> audio, CancellationToken ct = default)
        {
            Asked++;
            Handed.Add((segment, audio.Length));
            if (Blocked is { } held)
            {
                await held.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return new Transcribed(segment, text, 0.9);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
