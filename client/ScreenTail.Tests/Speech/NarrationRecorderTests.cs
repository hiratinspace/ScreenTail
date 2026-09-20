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

        await recorder.RunAsync(TestContext.Current.CancellationToken);

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

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(kept);
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

            foreach (var frame in _frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRecogniser(string text) : ISpeechRecogniser
    {
        public bool Ready { get; set; } = true;

        public int Asked { get; private set; }

        public List<(SpeechSegment Segment, int Samples)> Handed { get; } = [];

        public Task<Transcribed?> TranscribeAsync(SpeechSegment segment, ReadOnlyMemory<short> audio, CancellationToken ct = default)
        {
            Asked++;
            Handed.Add((segment, audio.Length));
            return Task.FromResult<Transcribed?>(new Transcribed(segment, text, 0.9));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
