using ScreenTail.Core.Speech;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-027: what reaches the transcript, and what must not (INV-9).
///
/// The hallucination cases are the point. ST-061 verifies a drafted quotation against the transcript, so a
/// line the model invented does not merely add noise — it makes a fabricated quote pass the check and get
/// published into a customer's ticket as something the technician said.
/// </summary>
public sealed class TranscriptAssemblerTests
{
    [Fact]
    public void RealSpeechIsKeptWithTheTechnicianAsTheSpeaker()
    {
        var segment = Assert.IsType<TranscriptSegment>(
            new TranscriptAssembler().Add(Heard("Restarting the print spooler now.", 4000, 0.92)));

        Assert.Equal("Restarting the print spooler now.", segment.Text);
        Assert.Equal(Speaker.Tech, segment.Speaker);
        Assert.Equal(0.92, segment.Confidence);
    }

    [Fact]
    public void NothingCanProduceAnEndUserSpeaker()
    {
        // INV-9. v1 captures one microphone and end_user is reserved for the v1.2 consent workflow, so
        // there is no parameter here to get wrong - a future change has to go through that ticket.
        var assembler = new TranscriptAssembler();

        var speakers = Lines
            .Select((text, i) => assembler.Add(Heard(text, 3000, 0.9, startMs: i * 5000)))
            .Select(segment => segment!.Speaker);

        Assert.All(speakers, speaker => Assert.Equal(Speaker.Tech, speaker));
    }

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("Thanks for watching!")]
    [InlineData("Subtitles by the Amara.org community")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("♪♪♪")]
    [InlineData("Please subscribe to the channel")]
    public void WhatWhisperSaysWhenItHasNothingToSayIsDropped(string invented)
    {
        // Whisper was trained on subtitle tracks, which is why its inventions are the phrases a video ends
        // with, and it produces them most on the quiet stretches a support call is mostly made of.
        Assert.Null(new TranscriptAssembler().Add(Heard(invented, durationMs: 900, confidence: 0.4)));
    }

    [Fact]
    public void APoliteTechnicianIsNotMistakenForAHallucination()
    {
        // The other half, and the reason a known phrase alone is not enough to drop a segment. People do
        // say thank you on support calls, and silently deleting it is its own kind of wrong transcript.
        var segment = new TranscriptAssembler().Add(Heard("Thank you, that's it sorted.", 3200, 0.88));

        Assert.NotNull(segment);
        Assert.Equal("Thank you, that's it sorted.", segment.Text);
    }

    [Fact]
    public void AKnownPhraseFromConfidentLongAudioIsKept()
    {
        // "Thank you" after four seconds of speech, at high confidence, is a technician being polite.
        Assert.NotNull(new TranscriptAssembler().Add(Heard("Thank you.", durationMs: 4000, confidence: 0.93)));
    }

    [Fact]
    public void ASentenceNobodyHadTimeToSayIsDropped()
    {
        // The giveaway is characters per second, not the words. Ordinary speech is about 15; this is a
        // fluent sentence attached to under a second of audio.
        var assembler = new TranscriptAssembler();

        Assert.Null(assembler.Add(Heard(
            "I have now restarted the print spooler service and cleared the entire queue.",
            durationMs: 800,
            confidence: 0.95)));
        Assert.Equal(1, assembler.Rejected);
    }

    [Fact]
    public void AnUnsureFragmentIsDroppedButAnUnsureSentenceIsNot()
    {
        // A three-word guess carries no meaning worth the risk of inventing one. A whole low-confidence
        // sentence is a technician in a noisy office, and the confidence travels with it to Review.
        Assert.Null(new TranscriptAssembler().Add(Heard("the uh", 2000, 0.3)));

        var kept = new TranscriptAssembler().Add(Heard("I think the driver is the wrong version here.", 3500, 0.35));
        Assert.NotNull(kept);
        Assert.Equal(0.35, kept.Confidence);
    }

    [Fact]
    public void SegmentsThatFinishOutOfOrderAreStillStoredInOrder()
    {
        // Transcription runs on a queue and a short segment can finish after a long one that came first.
        // ts_ms orders the whole session and the drafting prompt reads the transcript in order, so a
        // segment landing before the one it followed would reorder the technician's own account.
        var assembler = new TranscriptAssembler();

        var first = assembler.Add(Heard("Checked the spooler.", 4000, 0.9, startMs: 10_000))!;
        var second = assembler.Add(Heard("Started it again.", 2000, 0.9, startMs: 9_000))!;

        Assert.True(second.TsMs > first.EndMs, $"{second.TsMs} should follow {first.EndMs}");
        Assert.True(second.EndMs >= second.TsMs);
    }

    [Fact]
    public void IdsAreUniqueAndOrdered()
    {
        var assembler = new TranscriptAssembler();

        var ids = Enumerable.Range(0, 3)
            .Select(i => assembler.Add(Heard($"Step {i} done here.", 3000, 0.9, startMs: i * 5000))!.Id)
            .ToList();

        Assert.Equal(["t-0001", "t-0002", "t-0003"], ids);
    }

    [Fact]
    public void WhitespaceIsCollapsedSoTheTextComparesAsWords()
    {
        // ST-061 checks a drafted quotation against this text. Ragged whitespace would make a genuine
        // quotation fail to match the line it came from.
        var segment = new TranscriptAssembler().Add(Heard("  Restarted   the\n spooler.  ", 3000, 0.9))!;

        Assert.Equal("Restarted the spooler.", segment.Text);
    }

    [Fact]
    public void EmptyTextIsDroppedWithoutCountingAsSpeech()
    {
        var assembler = new TranscriptAssembler();

        Assert.Null(assembler.Add(Heard("   ", 3000, 0.9)));
        Assert.Equal(0, assembler.Kept);
        Assert.Equal(1, assembler.Rejected);
    }

    [Fact]
    public void WhatIsStoredPassesTheSchema()
    {
        // The store writes these straight into a session document, so a segment that breaks session.v1
        // fails at save time, after the audio is gone.
        var assembler = new TranscriptAssembler();
        var session = ReviewFixtureSession(assembler);

        Assert.Empty(SessionValidator.Validate(session));
    }

    private static Session ReviewFixtureSession(TranscriptAssembler assembler) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s-1",
        StartedAt = DateTimeOffset.UnixEpoch,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Screenconnect },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = [],
        Frames = [],
        Transcript =
        [
            assembler.Add(Heard("Found the spooler stopped.", 3000, 0.9, startMs: 1000))!,
            assembler.Add(Heard("Started it again.", 2500, 0.8, startMs: 9000))!,
        ],
    };

    private static readonly string[] Lines = ["Checked the service.", "Started it again.", "Printed a test page."];

    private static Transcribed Heard(string text, long durationMs, double confidence, long startMs = 1000) =>
        new(new SpeechSegment(startMs, startMs + durationMs, Truncated: false), text, confidence);
}
