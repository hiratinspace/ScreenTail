using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-027's voice-activity gate. ADR-0001 finding 8 measured Whisper using 49–62% of eight threads when
/// fed continuously, against ST-031's 15% for all of ScreenTail, so what this keeps and what it throws
/// away is the difference between the speech pipeline being affordable and not.
///
/// The tests play streams of probabilities rather than audio, because that is all the gate sees. Every
/// number here is a threshold somebody will want to argue with later, so each test says what would go
/// wrong if it moved.
/// </summary>
public sealed class SpeechGateTests
{
    private static readonly SpeechGateOptions Defaults = new();

    [Fact]
    public void SilenceProducesNothing()
    {
        // A technician types, reads and waits far more than they talk. If silence produced segments, the
        // pipeline would be busy for the whole session and ST-031's budget would be gone before speech.
        var gate = new SpeechGate();

        var segments = Play(gate, Quiet(300));

        Assert.Empty(segments);
        Assert.Equal(0, gate.FramesKept);
    }

    [Fact]
    public void ASentenceBecomesOneSegment()
    {
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Quiet(30), .. Loud(60), .. Quiet(60)]);

        var segment = Assert.Single(segments);
        Assert.False(segment.Truncated);
        Assert.True(segment.DurationMs >= 60 * 32, $"a two-second sentence came back as {segment.DurationMs} ms");
    }

    [Fact]
    public void APauseBetweenWordsDoesNotCutTheSentenceInTwo()
    {
        // People pause between clauses. Closing on the first quiet frame would hand Whisper half-sentences,
        // which costs accuracy as well as time — it reads a whole segment for context.
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Loud(30), .. Quiet(10), .. Loud(30), .. Quiet(60)]);

        Assert.Single(segments);
    }

    [Fact]
    public void ARealPauseEndsTheSegment()
    {
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Loud(30), .. Quiet(60), .. Loud(30), .. Quiet(60)]);

        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public void AProbabilityHoveringAtTheThresholdDoesNotChopTheSentenceUp()
    {
        // Why there are two thresholds rather than one. A single one, with a probability wobbling across
        // it, would open and close a segment every other frame and pay Whisper's fixed per-segment cost a
        // dozen times for one sentence.
        var gate = new SpeechGate();
        var wobbling = Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? 0.62 : 0.45).ToArray();

        var segments = Play(gate, [.. wobbling, .. Quiet(60)]);

        Assert.Single(segments);
    }

    [Fact]
    public void ASegmentKeepsTheAudioFromJustBeforeItOpened()
    {
        // Voice activity is recognised a frame or two after speech starts. Without the preroll every
        // segment loses its first consonant and "restart" is transcribed as "estart".
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Quiet(30), .. Loud(40), .. Quiet(60)]);

        var segment = Assert.Single(segments);
        var speechStartedAt = 30 * 32;
        Assert.True(
            segment.StartMs < speechStartedAt,
            $"the segment started at {segment.StartMs} ms, after speech began at {speechStartedAt} ms");
        Assert.Equal(speechStartedAt - (long)Defaults.Preroll.TotalMilliseconds, segment.StartMs);
    }

    [Fact]
    public void ACoughIsNotASegment()
    {
        // Short bursts are chairs, keyboards and throats. Transcribing them costs the fixed per-segment
        // price to produce nothing — or worse, to hallucinate a word onto noise, which then reaches a note.
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Quiet(30), .. Loud(3), .. Quiet(60)]);

        Assert.Empty(segments);
        Assert.Equal(1, gate.TooShort);
    }

    [Fact]
    public void SomebodyTalkingForAVeryLongTimeIsCutIntoPieces()
    {
        // ST-027 budgets five seconds of lag. A segment that only ends when the speaker does is a segment
        // that is never transcribed, and Whisper's cost grows with length, so it would block the queue too.
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Loud(3_000), .. Quiet(60)]);

        Assert.True(segments.Count > 1, "ninety seconds of continuous speech came back as one segment");
        Assert.Contains(segments, s => s.Truncated);
        Assert.All(segments, s => Assert.True(
            s.DurationMs <= Defaults.MaximumSegment.TotalMilliseconds + Defaults.FrameLength.TotalMilliseconds,
            $"a segment ran to {s.DurationMs} ms"));
    }

    [Fact]
    public void CuttingALongSegmentDoesNotDropTheWordsAcrossTheJoin()
    {
        // The cut happens mid-speech, so the next segment has to start where the last one ended rather
        // than waiting for speech to restart — otherwise a sentence disappears at every boundary.
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Loud(3_000), .. Quiet(60)]);

        for (var i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].EndMs, segments[i].StartMs);
        }
    }

    [Fact]
    public void TheTrailingSilenceIsNotPartOfTheSegment()
    {
        // The hangover is what proves speech ended; it is not speech. Including it would put a second of
        // nothing on the end of every segment, and ST-028 aligns frames against these timestamps.
        var gate = new SpeechGate();

        var segments = Play(gate, [.. Loud(40), .. Quiet(120)]);

        var segment = Assert.Single(segments);
        var speechEndedAt = 40 * 32;
        Assert.True(
            segment.EndMs <= speechEndedAt + Defaults.FrameLength.TotalMilliseconds,
            $"the segment ran to {segment.EndMs} ms although speech stopped at {speechEndedAt} ms");
    }

    [Fact]
    public void WhatIsStillOpenWhenTheSessionStopsIsNotLost()
    {
        // Stop is pressed while the technician is still explaining what they did. Without a flush, the
        // last thing they said would be held open forever and never reach the note.
        var gate = new SpeechGate();
        Play(gate, Loud(40));

        var final = gate.Flush();

        Assert.NotNull(final);
        Assert.True(final!.DurationMs > 0);
        Assert.Null(gate.Flush());
    }

    [Fact]
    public void MostOfASessionIsNeverHandedToTheTranscriber()
    {
        // The number ADR-0001 finding 8 asks for. A realistic session — a minute of talk in ten minutes of
        // work — should reach the transcriber as a small fraction of the audio.
        var gate = new SpeechGate();
        var stream = new List<double>();
        for (var minute = 0; minute < 10; minute++)
        {
            stream.AddRange(Loud(110));    // ~3.5 s of speech
            stream.AddRange(Quiet(1_765)); // ~56 s of working in silence
        }

        Play(gate, [.. stream]);

        var kept = gate.FramesKept / (double)gate.FramesSeen;
        Assert.True(kept < 0.15, $"the gate handed on {kept:P0} of the audio; ST-031 cannot afford that");
        Assert.True(kept > 0, "the gate handed on nothing at all");
    }

    [Fact]
    public void AFreshSessionStartsWithNoHistory()
    {
        var gate = new SpeechGate();
        Play(gate, [.. Loud(40), .. Quiet(60)]);

        gate.Reset();

        Assert.Equal(0, gate.FramesSeen);
        Assert.Equal(0, gate.FramesKept);
        Assert.False(gate.Speaking);
    }

    private static List<SpeechSegment> Play(SpeechGate gate, IReadOnlyList<double> probabilities)
    {
        var segments = new List<SpeechSegment>();
        foreach (var p in probabilities)
        {
            if (gate.Offer(p) is { } segment)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private static double[] Loud(int frames) => [.. Enumerable.Repeat(0.92, frames)];

    private static double[] Quiet(int frames) => [.. Enumerable.Repeat(0.04, frames)];
}
