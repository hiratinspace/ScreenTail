using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The note as it will land (Spec v0.6.0 §5 S3): a step's time and the screenshot the ticket calls it
/// by, what an empty section does, and the line under the note. The words follow the backend's
/// NoteFormatter, so the editor and the ticket never say two different things.
/// </summary>
public sealed class NoteLandingTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 8, 14, 2, 11, TimeSpan.FromHours(-5));

    [Fact]
    public void AStepsTimeIsItsFirstScreenshotsOnTheSessionsOwnClock()
    {
        var session = Timed();

        // 14:02:11 plus 2 min 0.4 s, at the session's own offset, whatever the machine's zone is.
        Assert.Equal("14:04", NoteLanding.StepTime(session, ["f2"], ["t1"]));
    }

    [Fact]
    public void AStepWithNoScreenshotTakesItsTranscriptLinesTimeAndANewStepHasNone()
    {
        var session = Timed();

        Assert.Equal("14:03", NoteLanding.StepTime(session, ["gone"], ["t1"]));
        Assert.Null(NoteLanding.StepTime(session, [], []));
    }

    [Theory]
    [InlineData(new[] { "f2" }, new[] { "f1", "f2", "f3" }, "screenshot 2")]
    [InlineData(new[] { "f3", "f1" }, new[] { "f1", "f2", "f3" }, "screenshots 1, 3")]
    [InlineData(new[] { "f3" }, new[] { "f1", "f3" }, "screenshot 2")]
    [InlineData(new[] { "f2" }, new[] { "f1", "f3" }, null)]
    [InlineData(new string[0], new[] { "f1" }, null)]
    public void AStepsScreenshotIsCalledByItsPlaceAmongTheAttachedOnesAsOnTheTicket(string[] refs, string[] attached, string? expected) =>
        Assert.Equal(expected, NoteLanding.ScreenshotPlaces(refs, attached));

    [Fact]
    public void TheMaskingCountSaysWhatIsMaskedAndWhatNoStepShows()
    {
        var onSteps = new HashSet<string>(["f1", "f2"], StringComparer.Ordinal);

        Assert.Equal("Nothing masked", NoteLanding.Masking([Frame("f1", 0)], onSteps));
        Assert.Equal("1 item masked, none left to review", NoteLanding.Masking([Frame("f1", 1)], onSteps));
        Assert.Equal("2 items masked, none left to review", NoteLanding.Masking([Frame("f1", 1), Frame("f2", 1)], onSteps));
        Assert.Equal("3 items masked, 2 on a screenshot no step shows", NoteLanding.Masking([Frame("f1", 1), Frame("f9", 2)], onSteps));
        Assert.Equal("2 items masked, 2 on screenshots no step shows", NoteLanding.Masking([Frame("f8", 1), Frame("f9", 1)], onSteps));
    }

    [Fact]
    public void TheLineUnderTheNoteIsTheMaskingThenTheSaveState()
    {
        Assert.Equal("2 items masked, none left to review · Saved", NoteLanding.Footer("2 items masked, none left to review", "Saved"));
        Assert.Equal("Nothing masked", NoteLanding.Footer("Nothing masked", string.Empty));
        Assert.Equal("Nothing masked", NoteLanding.Footer("Nothing masked", null));
    }

    [Theory]
    [InlineData("screenshot 2", true, false, "screenshot 2")]
    [InlineData(null, false, false, "not attached")]
    [InlineData(null, false, true, "withheld")]
    public void AStepsFrameSaysWhereItGoes(string? place, bool included, bool withheld, string expected) =>
        Assert.Equal(expected, NoteLanding.FrameState(place, included, withheld));

    [Fact]
    public void TheCopyHasNoDashes() =>
        Assert.All(
            [NoteLanding.Guide, NoteLanding.Inferred, NoteLanding.EmptySection],
            line => Assert.DoesNotContain("—", line, StringComparison.Ordinal));

    private static Session Timed() => Session(draft: Draft()) with
    {
        StartedAt = Started,
        Frames = [Frame("f1", 0) with { TsMs = 4_200 }, Frame("f2", 0) with { TsMs = 120_400 }],
        Transcript = [new TranscriptSegment { Id = "t1", TsMs = 61_000, EndMs = 64_000, Speaker = Speaker.Tech, Text = "Started it again." }],
    };

    private static Frame Frame(string id, int masked) => new()
    {
        Id = id,
        TsMs = 0,
        Trigger = FrameTrigger.Click,
        Image = $"{id}.png",
        Width = 1920,
        Height = 1080,
        RedactionPending = false,
        MaskedRegions = [.. Enumerable.Range(0, masked).Select(_ => new MaskedRegion { X = 0, Y = 0, Width = 10, Height = 10, Kind = MaskKind.Password })],
        SensitiveContext = false,
        ExcludedByUser = false,
    };
}
