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
        // and was appended. The gate is cut at the boundary, so what was being said before it is kept on
        // its own and nothing heard after the resume can finish it.
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
        //
        // Two things said, the first refused by the store. The second has to arrive: a counter that
        // went up proves the failure was seen, not that narration went on (2026-10-12 review, T-95).
        var kept = new List<TranscriptSegment>();
        var refusals = 0;
        var recogniser = new FakeRecogniser("restarting the spooler");
        var recorder = new NarrationRecorder(
            new FakeMicrophone(Silence(20), Speech(40), Silence(40), Speech(40), Silence(40)),
            recogniser,
            (_, segment, _) =>
            {
                refusals++;
                return refusals == 1
                    ? throw new InvalidOperationException("UNIQUE constraint failed: transcript.id")
                    : Task.FromResult(Add(kept, segment));
            });

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, recorder.Failures);
        Assert.Equal(2, recogniser.Asked);
        Assert.Equal("restarting the spooler", Assert.Single(kept).Text);
        Assert.Equal(1, recorder.Kept);
    }

    [Fact]
    public async Task TheLastThingSaidBeforeStopReachesTheSession()
    {
        // 2026-10-12 review (T-93). The test above this one ends the session by running the microphone
        // out, and that path flushed the gate. A real Stop ends it by the session going away between two
        // chunks, and that path reset the gate instead: whatever was being said when the technician
        // pressed Stop was dropped, and it is usually the outcome. The words were heard while recording,
        // so they belong to the session (INV-6 is about what is heard after the boundary, and nothing
        // after it is included). #188 made the machine accept them while it finalizes; this is the
        // recorder's half.
        var microphone = new LiveMicrophone(Silence(20)) { KeepsSaying = Speech(1)[0] };
        var kept = new List<TranscriptSegment>();
        NarrationSession? session = new("session-a", 0);
        var recorder = new NarrationRecorder(
            microphone,
            new FakeRecogniser("and that fixed it"),
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => session);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);

        // Two seconds into a sentence that has not ended, the technician presses Stop.
        await UntilAsync(() => microphone.Yielded >= 80);
        session = null;
        await UntilAsync(() => !recorder.Listening);
        await UntilAsync(() => kept.Count >= 1);

        await stop.CancelAsync();
        await running;

        var last = Assert.Single(kept);
        Assert.Equal("and that fixed it", last.Text);
        Assert.Equal(1, microphone.Closes);
    }

    [Fact]
    public async Task ASentenceStillBeingSaidAtStopIsOwedSoTheNoteWaitsForIt()
    {
        // The other half of T-93. Finalize waits for what the backlog says the session is owed, and the
        // recorder only cuts the open sentence on the chunk after Stop. A sentence not yet cut was owed
        // nothing, so a Stop whose sources closed within one audio buffer drafted the note without it.
        // It is counted from the moment the gate opens, and the count is what the cut segment inherits.
        var backlog = new NarrationBacklog();
        var microphone = new FakeMicrophone(Silence(20), Speech(60));
        var kept = new List<TranscriptSegment>();
        int? owedWhenStopped = null;
        var recorder = new NarrationRecorder(
            microphone,
            new FakeRecogniser("and that fixed it"),
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () =>
            {
                // Recording for sixty frames, forty of them mid-sentence, then Stop. Asked of each chunk
                // before it is judged, so this is the moment the recorder learns of the Stop.
                if (microphone.Yielded <= 60)
                {
                    return new NarrationSession("session-a", 0);
                }

                owedWhenStopped ??= backlog.DepthFor("session-a");
                return null;
            },
            backlog: backlog);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => kept.Count >= 1 && backlog.DepthFor("session-a") == 0);
        await stop.CancelAsync();
        await running;

        Assert.Equal(1, owedWhenStopped);
        Assert.Equal("and that fixed it", Assert.Single(kept).Text);
        Assert.Equal(0, backlog.DepthFor("session-a"));
    }

    [Fact]
    public async Task ASentenceDroppedAtASuppressionIsNoLongerOwed()
    {
        // What the gate was holding when an excluded app came to the front is forgotten, so nothing is
        // owed for it: a Stop during the vault must not wait out its grace for a sentence that will never
        // be transcribed.
        var backlog = new NarrationBacklog();
        var owed = new List<int>();
        var recorder = new NarrationRecorder(
            new FakeMicrophone(Silence(20), Speech(20), Speech(20), Silence(40)),
            new FakeRecogniser("the admin password is"),
            (_, _, _) => Task.FromResult(true),
            () => new NarrationSession("session-a", 0),
            hearing: () =>
            {
                owed.Add(backlog.DepthFor("session-a"));
                return owed.Count is <= 40 or > 60;
            },
            backlog: backlog);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        // Asked before chunk 41 is judged: twenty frames into the sentence, so it is owed. Asked before
        // chunk 42: the vault came to the front on 41, so it is not.
        Assert.Equal(1, owed[40]);
        Assert.Equal(0, owed[41]);
        Assert.Equal(0, backlog.DepthFor("session-a"));
    }

    [Fact]
    public async Task SpeechFromBeforeAPauseIsNotJoinedToSpeechAfterIt()
    {
        // 2026-10-12 review (T-94). The pause test above never opens a segment, so what happens to one
        // that is open at a pause was unpinned. It is cut at the pause: the words from before it reach
        // the session on their own, nothing heard afterwards is joined on to them, and what is said after
        // the resume is its own segment, placed after the first.
        var microphone = new LiveMicrophone(Silence(20)) { KeepsSaying = Speech(1)[0] };
        var recogniser = new FakeRecogniser("restarting the spooler");
        var kept = new List<TranscriptSegment>();
        var recording = true;
        var recorder = Recorder(microphone, recogniser, kept, recording: () => recording);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => microphone.Yielded >= 80);

        // A pause mid-sentence: a password field, or the technician's own hand.
        recording = false;
        var heardBeforePause = microphone.Yielded;
        await UntilAsync(() => !recorder.Listening);
        await UntilAsync(() => kept.Count >= 1);

        // Capture comes back and the technician says something else.
        microphone.Replay(Silence(20), Speech(40), Silence(40));
        microphone.KeepsSaying = new short[Rate * 32 / 1000];
        recording = true;
        recorder.Nudge();
        await UntilAsync(() => kept.Count >= 2);

        await stop.CancelAsync();
        await running;

        Assert.Equal(2, kept.Count);
        Assert.Equal(2, microphone.Opens);
        Assert.Equal(microphone.Opens, microphone.Closes);

        // The first segment was cut from audio heard before the pause and no later. A few frames of
        // slack, because the recorder notices the pause on the chunk after it.
        var frame = Rate * 32 / 1000;
        var first = recogniser.Handed[0];
        Assert.True(
            first.Samples <= (heardBeforePause + 8) * frame,
            $"the first segment held {first.Samples} samples, more than the {heardBeforePause} frames heard before the pause");
        Assert.True(kept[1].TsMs >= kept[0].EndMs, "the second segment should follow the first on the session's clock");
    }

    [Fact]
    public async Task TheMicrophoneIsClosedWhenTheSessionEnds()
    {
        // 2026-10-12 review (T-236). Opening once per session is half of what keeps the microphone-in-use
        // indicator honest; letting go of the device at the end of the stretch is the other half, and
        // nothing pinned it. The recorder closes the device by returning from its enumerator, so a
        // microphone that counts its disposals must see one close per open.
        var microphone = new LiveMicrophone(Silence(20), Speech(40), Silence(40));
        var kept = new List<TranscriptSegment>();
        var recording = true;
        var recorder = Recorder(microphone, new FakeRecogniser("restarting the spooler"), kept, recording: () => recording);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => kept.Count >= 1);

        recording = false;
        await UntilAsync(() => microphone.Closes >= 1);

        Assert.Equal(1, microphone.Opens);
        Assert.Equal(1, microphone.Closes);
        Assert.False(recorder.Listening);

        recording = true;
        recorder.Nudge();
        await UntilAsync(() => microphone.Opens >= 2);

        await stop.CancelAsync();
        await running;

        Assert.Equal(2, microphone.Opens);
        Assert.Equal(microphone.Opens, microphone.Closes);
    }

    [Fact]
    public async Task TheRoomIsLearnedAgainWithEachSession()
    {
        // 2026-10-12 review (E-8): VoiceActivity.Reset had no caller. One session in a quiet office and
        // the next beside a server rack: the floor learned for the first makes the second room read as
        // speech for half a minute, and that half minute goes to a transcriber that answers room tone
        // with invented sentences. A new session starts by listening to its own room.
        var microphone = new LiveMicrophone(Silence(20), Speech(40), Silence(40));
        var recogniser = new FakeRecogniser("restarting the spooler");
        var kept = new List<TranscriptSegment>();
        NarrationSession? session = new("session-a", 0);
        var recorder = new NarrationRecorder(
            microphone,
            recogniser,
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => session);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => kept.Count >= 1);

        session = null;
        await UntilAsync(() => !recorder.Listening);

        // The next session, in a much louder room where nobody says anything.
        var heard = microphone.Yielded;
        microphone.Replay(Room(3000, 200));
        session = new NarrationSession("session-b", 0);
        recorder.Nudge();
        await UntilAsync(() => microphone.Yielded >= heard + 230);

        await stop.CancelAsync();
        await running;

        Assert.Equal(1, recogniser.Asked);
        Assert.Equal(1, recorder.Heard);
        Assert.Single(kept);
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

    [Fact]
    public async Task SpeechAfterAPauseIsStillTranscribed()
    {
        // 2026-10-12 review. Discard emptied the ring and its sample counter at every pause, suppression
        // and session end, and left the gate's frame clock running. Every segment after the first such
        // boundary then pointed at ring positions that were never written, came out empty, and was
        // counted as missed: narration ended for the life of the service after the first pause.
        var microphone = new LiveMicrophone(Silence(20), Speech(40), Silence(40));
        var kept = new List<TranscriptSegment>();
        var recording = true;
        var recorder = Recorder(microphone, new FakeRecogniser("restarting the spooler"), kept, recording: () => recording);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => kept.Count >= 1);
        Assert.Single(kept);

        // A pause (a password field, or the session ending): the microphone closes.
        recording = false;
        await UntilAsync(() => !recorder.Listening);
        Assert.False(recorder.Listening);

        // Capture comes back and the technician speaks again.
        recording = true;
        recorder.Nudge();
        await UntilAsync(() => kept.Count >= 2);

        await stop.CancelAsync();
        await running;

        Assert.Equal(2, microphone.Opens);
        Assert.Equal(0, recorder.MissedWaitingForModel);
        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public async Task TranscriptTimesStartAgainWithEachSession()
    {
        // ts_ms is session time: clicks and frames are stamped from the session's start, and ST-028
        // aligns speech against them. The gate counted from the service's start instead, so the second
        // session's narration was placed minutes or hours after anything else in it.
        var microphone = new LiveMicrophone(Silence(20), Speech(40), Silence(40));
        var kept = new List<(string Session, TranscriptSegment Segment)>();
        NarrationSession? session = new("session-a", 0);
        var recorder = new NarrationRecorder(
            microphone,
            new FakeRecogniser("restarting the spooler"),
            (heardIn, segment, _) =>
            {
                kept.Add((heardIn, segment));
                return Task.FromResult(true);
            },
            () => session);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => kept.Count >= 1);

        session = null;
        await UntilAsync(() => !recorder.Listening);

        session = new NarrationSession("session-b", 0);
        recorder.Nudge();
        await UntilAsync(() => kept.Count >= 2);

        await stop.CancelAsync();
        await running;

        Assert.Equal(2, kept.Count);
        var (firstSession, first) = kept[0];
        var (secondSession, second) = kept[1];
        Assert.Equal("session-a", firstSession);
        Assert.Equal("session-b", secondSession);

        // The same words at the same moment of two sessions are at the same place on each timeline, and
        // the second is not pushed after the first session's last word either.
        Assert.Equal(first.TsMs, second.TsMs);
        Assert.Equal(first.EndMs, second.EndMs);
        Assert.InRange(second.TsMs, 0, 1_000);
    }

    [Fact]
    public async Task WhatIsSaidAfterAPauseLandsWhereItWasSaid()
    {
        // The other half of session time. The gate's clock restarts when listening does, so what is said
        // after a pause has to be placed by the session's own clock at that moment, not at zero again.
        var microphone = new LiveMicrophone(Silence(20), Speech(40), Silence(40));
        var kept = new List<TranscriptSegment>();
        NarrationSession? session = new("session-a", 0);
        var recorder = new NarrationRecorder(
            microphone,
            new FakeRecogniser("restarting the spooler"),
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => session);
        using var stop = new CancellationTokenSource();

        var running = recorder.RunAsync(stop.Token);
        await UntilAsync(() => kept.Count >= 1);

        session = null;
        await UntilAsync(() => !recorder.Listening);

        // A minute later on the session's clock, the same session resumes.
        session = new NarrationSession("session-a", 60_000);
        recorder.Nudge();
        await UntilAsync(() => kept.Count >= 2);

        await stop.CancelAsync();
        await running;

        Assert.Equal(2, kept.Count);
        Assert.Equal(kept[0].TsMs + 60_000, kept[1].TsMs);
        Assert.NotEqual(kept[0].Id, kept[1].Id);
    }

    [Fact]
    public async Task NarrationQueuedInOneSessionIsNeverWrittenIntoTheNext()
    {
        // 2026-10-12 review. A queued segment carried no session, and the append looked the session up
        // when the model finished. With a few segments queued and the model taking seconds each, the
        // next customer's session could be recording by then, and it got the last customer's words.
        var microphone = new FakeMicrophone(Silence(20), Speech(40), Silence(40));
        var recogniser = new FakeRecogniser("the server is called acme-dc01") { Blocked = new TaskCompletionSource() };
        var written = new List<string>();
        var session = "session-a";
        var recorder = new NarrationRecorder(
            microphone,
            recogniser,
            (heardIn, _, _) =>
            {
                written.Add(heardIn);
                return Task.FromResult(true);
            },
            () => new NarrationSession(session, 0));

        var running = recorder.RunAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => recogniser.Asked == 1);
        Assert.Equal(1, recogniser.Asked);

        // The model is still busy with session A's sentence when session B begins.
        session = "session-b";
        recogniser.Blocked.SetResult();
        await running;

        Assert.Equal(["session-a"], written);
    }

    [Fact]
    public async Task WhatIsStillWithTheModelIsOwedToItsSession()
    {
        // The recorder's half of finalize's wait: a segment is owed to its session from the moment it is
        // cut until it has been written or refused, and no longer.
        var backlog = new NarrationBacklog();
        var recogniser = new FakeRecogniser("restarting the spooler") { Blocked = new TaskCompletionSource() };
        var kept = new List<TranscriptSegment>();
        var recorder = new NarrationRecorder(
            new FakeMicrophone(Silence(20), Speech(40), Silence(40)),
            recogniser,
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => new NarrationSession("session-a", 0),
            backlog: backlog);

        var running = recorder.RunAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => recogniser.Asked == 1);
        Assert.Equal(1, backlog.DepthFor("session-a"));

        recogniser.Blocked.SetResult();
        await running;

        Assert.Single(kept);
        Assert.Equal(0, backlog.DepthFor("session-a"));
        Assert.False(backlog.Wants("session-a"));
    }

    [Fact]
    public async Task WhatIsQueuedForADiscardedSessionIsNeverTranscribed()
    {
        // Discarding a session forgets what it is owed. A segment still queued for it is dropped rather
        // than handed to the model: nobody can store it, and it is a piece of the customer's call held
        // in memory for nothing.
        var backlog = new NarrationBacklog();
        var recogniser = new FakeRecogniser("restarting the spooler") { Blocked = new TaskCompletionSource() };
        var kept = new List<TranscriptSegment>();
        var microphone = new FakeMicrophone(Silence(20), Speech(40), Silence(40), Speech(40), Silence(40));
        var recorder = new NarrationRecorder(
            microphone,
            recogniser,
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => new NarrationSession("session-a", 0),
            backlog: backlog);

        var running = recorder.RunAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => backlog.DepthFor("session-a") == 2);
        Assert.Equal(2, backlog.DepthFor("session-a"));

        Assert.Equal(2, backlog.Forget("session-a"));
        recogniser.Blocked.SetResult();
        await running;

        // The first was already with the model; the second never reached it.
        Assert.Equal(1, recogniser.Asked);
        Assert.Equal(0, backlog.DepthFor("session-a"));
    }

    [Fact]
    public async Task NothingSaidWhileAnExcludedAppIsInFrontIsTranscribed()
    {
        // 2026-10-12 review. With a password manager in front, frames and keystrokes were dropped and
        // speech was not: "the admin password is ..." read off the vault went through the model, into
        // the store, the bundle and the draft.
        var recogniser = new FakeRecogniser("the admin password is hunter2");
        var kept = new List<TranscriptSegment>();
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(40), Silence(40)),
            recogniser,
            kept,
            hearing: () => false);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(kept);
        Assert.Equal(0, recogniser.Asked);
        Assert.Equal(0, recorder.Heard);
    }

    [Fact]
    public async Task ASentenceStartedBeforeAnExcludedAppCameToFrontIsNotFinishedAfterIt()
    {
        // The vault comes to the front mid-sentence and the technician reads the rest off it. The words
        // said before it came to the front are dropped at that boundary, rather than kept as the start of
        // a segment that goes on to include what was read.
        var recogniser = new FakeRecogniser("the admin password is hunter2");
        var kept = new List<TranscriptSegment>();
        var chunks = 0;
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(20), Speech(20), Silence(40)),
            recogniser,
            kept,
            hearing: () =>
            {
                // In scope for the silence and the first words, excluded for the rest of the sentence,
                // back in scope for the quiet after it.
                chunks++;
                return chunks is <= 40 or > 60;
            });

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(kept);
        Assert.Equal(0, recogniser.Asked);
    }

    [Fact]
    public async Task SpeechFromBeforeASuppressionIsNotTranscribedAfterIt()
    {
        // 2026-10-12 review (T-94). The test above proves nothing reaches the model, which a recorder that
        // drops everything after the first suppression also passes. Here the technician speaks again once
        // the vault has gone: that sentence is transcribed, on its own, and the words from before the
        // vault came to the front are not handed over with it or after it.
        var recogniser = new FakeRecogniser("restarting the spooler");
        var kept = new List<TranscriptSegment>();
        var chunks = 0;
        var recorder = Recorder(
            new FakeMicrophone(Silence(20), Speech(20), Speech(20), Silence(40), Speech(40), Silence(40)),
            recogniser,
            kept,
            hearing: () =>
            {
                // In scope for the silence and the first words, excluded for the rest of that sentence,
                // back in scope for the quiet and the next sentence.
                chunks++;
                return chunks is <= 40 or > 60;
            });

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("restarting the spooler", Assert.Single(kept).Text);
        var handed = Assert.Single(recogniser.Handed);

        // The only segment is the second sentence. The gate's clock starts again when hearing does, so
        // it begins after the forty frames of quiet that followed the vault, less the pre-roll.
        var frameMs = (int)new SpeechGateOptions().FrameLength.TotalMilliseconds;
        Assert.True(
            handed.Segment.StartMs >= (40 * frameMs) - 250,
            $"the segment began at {handed.Segment.StartMs} ms, before the sentence said after the vault had gone");
        Assert.Equal(0, recorder.MissedWaitingForModel);
    }

    [Fact]
    public void AServiceRecorderCannotBeBuiltWithoutSayingWhenASessionIsRecording()
    {
        // 2026-10-12 review (T-84). "Is a session recording?" used to default to "always". A host that
        // dropped the argument still compiled, and the microphone was then open all day with everything
        // said between sessions sent to the model (INV-9). The constructor a service uses, the one that
        // shares a backlog with the session machine, cannot be called without it; the only other is
        // the all-narration one, which takes nothing a service passes.
        var constructors = typeof(NarrationRecorder).GetConstructors();
        var service = Assert.Single(constructors, c => c.GetParameters().Any(p => p.ParameterType == typeof(NarrationBacklog)));

        var recording = Assert.Single(service.GetParameters(), p => p.Name == "recording");
        Assert.False(recording.IsOptional, "the recording gate has a default again");

        var other = Assert.Single(constructors, c => c != service);
        Assert.Equal(
            [typeof(IMicrophone), typeof(ISpeechRecogniser), typeof(AppendTranscript)],
            other.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public async Task SpeechIsHeardAgainOnceTheExcludedAppHasGone()
    {
        // The control. Dropping speech while the vault is in front must not end narration, and the
        // microphone is not closed and reopened for it: the session is still recording.
        var recogniser = new FakeRecogniser("restarting the spooler");
        var kept = new List<TranscriptSegment>();
        var chunks = 0;
        var microphone = new FakeMicrophone(Silence(20), Speech(40), Silence(40), Silence(20), Speech(40), Silence(40));
        var recorder = Recorder(
            microphone,
            recogniser,
            kept,
            hearing: () => ++chunks > 100);

        await recorder.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("restarting the spooler", Assert.Single(kept).Text);
        Assert.Equal(1, recogniser.Asked);
        Assert.Equal(1, microphone.Opens);
        Assert.Equal(0, recorder.MissedWaitingForModel);
    }

    private static bool Add(List<TranscriptSegment> kept, TranscriptSegment segment)
    {
        kept.Add(segment);
        return true;
    }

    private static NarrationRecorder Recorder(
        IMicrophone microphone,
        FakeRecogniser recogniser,
        List<TranscriptSegment> kept,
        Func<bool>? recording = null,
        Func<bool>? hearing = null) =>
        new(
            microphone,
            recogniser,
            (_, segment, _) => Task.FromResult(Add(kept, segment)),
            () => recording is null || recording() ? new NarrationSession("session-a", 0) : null,
            hearing: hearing);

    /// <summary>Waits for a condition the recorder reaches on its own threads, for at most a few seconds.</summary>
    private static async Task UntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 250 && !done(); i++)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Frames of a quiet room, at the gate's frame length.
    ///
    /// Room tone, not digital zeros. A real microphone in a quiet room never delivers zeros; zeros are a
    /// mute or a stream that has not started, and the detector treats them as saying nothing about the
    /// room (2026-10-12 review). A fixture of zeros hid that: the room was never heard, so the first
    /// frame of speech became the floor and the bug could not show.
    /// </summary>
    internal static short[][] Silence(int frames) => Room(100, frames);

    /// <summary>Frames of steady noise at the given amplitude: what a room sounds like to the detector, loud or quiet.</summary>
    internal static short[][] Room(short amplitude, int frames)
    {
        var random = new Random(7);
        return Frames(frames, _ => (short)random.Next(-amplitude, amplitude + 1));
    }

    /// <summary>Frames of a 200 Hz tone well above the floor: what a voice looks like to the detector.</summary>
    internal static short[][] Speech(int frames)
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

    internal sealed class FakeMicrophone(params short[][][] runs) : IMicrophone
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

    /// <summary>
    /// A microphone that plays its script on every open and then keeps delivering one frame until closed,
    /// as a real one does. Digital silence by default; a test that wants a sentence still in progress when
    /// something happens sets <see cref="KeepsSaying"/> to speech.
    /// </summary>
    private sealed class LiveMicrophone(params short[][][] runs) : IMicrophone
    {
        private List<short[]> _frames = [.. runs.SelectMany(run => run)];

        public int Opens { get; private set; }

        /// <summary>
        /// How many times the device was let go of. Windows keeps the microphone-in-use indicator lit
        /// until this catches up with <see cref="Opens"/>, so it is the half of the count a customer sees.
        /// </summary>
        public int Closes { get; private set; }

        /// <summary>Frames delivered so far, across every open.</summary>
        public int Yielded { get; private set; }

        /// <summary>What is delivered once the script has run out, until the device is closed.</summary>
        public short[] KeepsSaying { get; set; } = new short[Rate * 32 / 1000];

        public string? DeviceName => "Live Fake Microphone";

        public int SampleRate => Rate;

        /// <summary>What the next open plays before <see cref="KeepsSaying"/> takes over.</summary>
        public void Replay(params short[][][] next) => _frames = [.. next.SelectMany(run => run)];

        public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            Opens++;
            try
            {
                foreach (var frame in _frames)
                {
                    ct.ThrowIfCancellationRequested();
                    Yielded++;
                    yield return frame;
                    await Task.Yield();
                }

                while (!ct.IsCancellationRequested)
                {
                    Yielded++;
                    yield return KeepsSaying;
                    await Task.Delay(1, CancellationToken.None);
                }
            }
            finally
            {
                // Disposing the enumerator is how the recorder closes the device, so this is where a real
                // microphone lets go of the endpoint.
                Closes++;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeRecogniser(string text) : ISpeechRecogniser
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
