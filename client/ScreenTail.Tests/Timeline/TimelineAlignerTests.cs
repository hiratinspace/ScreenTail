using System.Text.Json;
using Json.Schema;
using ScreenTail.Core.Timeline;
using ScreenTail.Tests.Schema;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Timeline;

/// <summary>
/// ST-028: which screenshot each thing the technician said was about.
///
/// This decides what a published ticket note claims. A sentence attached to the wrong screenshot is worse
/// than one attached to none — the note reads as if the technician said something about a step they never
/// mentioned, and it reads confidently.
/// </summary>
public sealed class TimelineAlignerTests
{
    [Fact]
    public void AClickAndWhatWasSaidAroundItAreLinked()
    {
        // ST-028's first criterion, verbatim: a click at 12.0 s and a segment spanning 10.5–14.2 s.
        var frames = new[] { Frame("f1", 12_000) };
        var segments = new[] { Segment("t1", 10_500, 14_200) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal("f1", Assert.Single(alignment.Segments).FrameId);
        Assert.Empty(alignment.Narration);
    }

    [Fact]
    public void SpeechThatStartsBeforeTheClickStillBelongsToIt()
    {
        // The common case, and the one a literal "nearest preceding frame" gets wrong: a technician says
        // "I'm restarting the spooler now" while clicking, so the sentence opens before the screenshot.
        // Anchoring on the segment's start and looking backwards would send this to narration.
        var frames = new[] { Frame("f1", 12_000) };
        var segments = new[] { Segment("t1", 9_000, 13_000) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal("f1", Assert.Single(alignment.Segments).FrameId);
    }

    [Fact]
    public void SpeechWithNoScreenshotNearItBecomesNarration()
    {
        // ST-028's second criterion. The technician explained something they did not screenshot — the
        // explanation still has to reach the note, as narration rather than as nothing.
        var frames = new[] { Frame("f1", 2_000) };
        var segments = new[] { Segment("t1", 40_000, 44_000) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Null(Assert.Single(alignment.Segments).FrameId);
        var narration = Assert.Single(alignment.Narration);
        Assert.Equal("t1", narration.SegmentId);
        Assert.Equal(40_000, narration.TsMs);
    }

    [Theory]
    [InlineData(7_900, "f1")]
    [InlineData(8_000, "f1")]
    [InlineData(8_100, null)]
    public void TheWindowIsEightSecondsFromTheEdgeOfWhatWasSaid(long gap, string? expected)
    {
        // Measured from the span, not from its start: the eight seconds is how far a screenshot may be
        // from the talking, and a long sentence is not further from a frame because it was long.
        var frames = new[] { Frame("f1", 0) };
        var segments = new[] { Segment("t1", gap, gap + 30_000) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal(expected, Assert.Single(alignment.Segments).FrameId);
    }

    [Fact]
    public void TheNearerOfTwoScreenshotsWins()
    {
        var frames = new[] { Frame("early", 10_000), Frame("late", 20_000) };
        var segments = new[] { Segment("t1", 18_000, 19_000) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal("late", Assert.Single(alignment.Segments).FrameId);
    }

    [Fact]
    public void ASentenceExactlyBetweenTwoScreenshotsBelongsToTheEarlierOne()
    {
        // People describe what they did more often than what they are about to do, so the tie goes
        // backwards. Without a rule it would go to whichever frame the list happened to yield first.
        var frames = new[] { Frame("before", 10_000), Frame("after", 20_000) };
        var segments = new[] { Segment("t1", 15_000, 15_000) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal("before", Assert.Single(alignment.Segments).FrameId);
    }

    [Fact]
    public void SeveralSentencesAboutOneScreenshotAllPointAtIt()
    {
        // A technician explains a step in three sentences. Each is its own segment and all three are about
        // the same picture; making frames exclusive would send two of them to narration.
        var frames = new[] { Frame("f1", 10_000) };
        var segments = new[]
        {
            Segment("t1", 9_000, 10_500),
            Segment("t2", 11_000, 12_500),
            Segment("t3", 13_000, 14_000),
        };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal(3, alignment.Linked);
        Assert.All(alignment.Segments, s => Assert.Equal("f1", s.FrameId));
        Assert.Empty(alignment.Narration);
    }

    [Fact]
    public void TheSameSessionAlignsTheSameWayEveryTime()
    {
        // A note that changes between two runs over one session is a note nobody can review. Frames and
        // segments arrive from the store in whatever order a query returns them.
        var frames = new[] { Frame("b", 10_000), Frame("a", 10_000), Frame("c", 30_000) };
        var segments = new[] { Segment("t2", 20_000, 21_000), Segment("t1", 11_000, 12_000) };

        var first = TimelineAligner.Align(segments, frames);
        var second = TimelineAligner.Align(segments.Reverse(), frames.Reverse());

        Assert.Equal(
            first.Segments.Select(s => (s.Id, s.FrameId)),
            second.Segments.Select(s => (s.Id, s.FrameId)));
    }

    [Fact]
    public void SegmentsComeBackInTimeOrder()
    {
        var frames = Array.Empty<Frame>();
        var segments = new[] { Segment("t3", 30_000, 31_000), Segment("t1", 1_000, 2_000), Segment("t2", 20_000, 21_000) };

        var alignment = TimelineAligner.Align(segments, frames);

        Assert.Equal(["t1", "t2", "t3"], alignment.Segments.Select(s => s.Id));
        Assert.Equal(["t1", "t2", "t3"], alignment.Narration.Select(n => n.SegmentId));
    }

    [Fact]
    public void ASessionWithNoScreenshotsIsAllNarration()
    {
        // A session where scope allowed no frames — every window out of scope — still has everything the
        // technician said, and Review shows it as narration rather than as an empty note.
        var segments = new[] { Segment("t1", 1_000, 2_000), Segment("t2", 9_000, 10_000) };

        var alignment = TimelineAligner.Align(segments, []);

        Assert.Equal(2, alignment.Narration.Count);
        Assert.Equal(0, alignment.Linked);
    }

    [Fact]
    public void ASessionWithNothingSaidProducesNothing()
    {
        var alignment = TimelineAligner.Align([], [Frame("f1", 1_000)]);

        Assert.Empty(alignment.Segments);
        Assert.Empty(alignment.Narration);
    }

    [Fact]
    public void AlignmentDoesNotChangeWhatWasSaid()
    {
        // The only field it may touch is the frame it points at. A step that reworded a technician's own
        // sentence would be a different kind of bug entirely.
        var original = Segment("t1", 10_000, 11_000) with { Text = "the spooler was stopped", Confidence = 0.91 };

        var aligned = Assert.Single(TimelineAligner.Align([original], [Frame("f1", 10_500)]).Segments);

        Assert.Equal(original.Text, aligned.Text);
        Assert.Equal(original.TsMs, aligned.TsMs);
        Assert.Equal(original.EndMs, aligned.EndMs);
        Assert.Equal(original.Speaker, aligned.Speaker);
        Assert.Equal(original.Confidence, aligned.Confidence);
    }

    [Fact]
    public void TheAlignedSessionStillValidatesAgainstTheSchema()
    {
        // ST-028's third criterion, against the real schema rather than a hand-checked shape. Alignment
        // writes frame_id onto segments and adds narration events, and both are shapes the bundle builder,
        // the drafter and Review all read — a session that no longer validates would break all three.
        var schema = SessionSchema.Instance;
        var frames = new[] { Frame("f1", 12_000) };
        var segments = new[] { Segment("t1", 10_500, 14_200), Segment("t2", 90_000, 92_000) };

        var alignment = TimelineAligner.Align(segments, frames);
        var session = SessionJson.Deserialize(
            File.ReadAllText(Path.Combine(SessionSchema.Root, "examples", "valid", "minimal-recording.json")))
            with
            {
                Frames = frames,
                Transcript = alignment.Segments,
                Events = [.. alignment.Narration],
            };

        var results = schema.Evaluate(
            JsonSerializer.Deserialize<JsonElement>(SessionJson.Serialize(session)),
            SessionSchema.Options);

        Assert.True(results.IsValid, JsonSerializer.Serialize(results));

        // And it is a real session, not an empty one that validates by having nothing in it.
        Assert.Equal("f1", session.Transcript[0].FrameId);
        Assert.Single(session.Events);
    }

    private static Frame Frame(string id, long tsMs) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = $"frames/{id}.jpg",
        Width = 800,
        Height = 600,
        RedactionPending = false,

        // A frame that is no longer pending has been through the worker, so it has a time it was
        // redacted at — session.v1 enforces the pair, and SessionJson refuses to write one without it.
        RedactedAt = new DateTimeOffset(2026, 9, 13, 15, 0, 0, TimeSpan.Zero),
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    private static TranscriptSegment Segment(string id, long tsMs, long endMs) => new()
    {
        Id = id,
        TsMs = tsMs,
        EndMs = endMs,
        Speaker = Speaker.Tech,
        Text = "the printer is offline",
    };
}
