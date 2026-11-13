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

    [Theory]
    [InlineData("screenshot 2", true, false, "screenshot 2")]
    [InlineData(null, false, false, "not attached")]
    [InlineData(null, false, true, "withheld")]
    public void AStepsFrameSaysWhereItGoes(string? place, bool included, bool withheld, string expected) =>
        Assert.Equal(expected, NoteLanding.FrameState(place, included, withheld));

    [Fact]
    public void AStepsFrameSaysWhatDelDoesWhereItSaysWhatSpaceDoes()
    {
        // Spec v0.6.8 \u00a75 S3: Del works on a step's row too, so its help says so, with the undo.
        Assert.Equal("Enter enlarges it. Space includes or excludes it. Del deletes it, with 5 seconds to undo (Ctrl+Z).", NoteLanding.FrameHelp);
        Assert.Equal("Enter or click to enlarge, B there to blur. Space includes or excludes it. Del deletes it, with 5 seconds to undo (Ctrl+Z).", NoteLanding.FrameTip);
    }

    [Theory]
    [InlineData(true, false, false, true)] // on the step's frame, or any button: the delete
    [InlineData(true, true, false, true)] // in a text box, nothing typed since Del: still the delete
    [InlineData(true, true, true, false)] // typed since: the text box's own undo
    [InlineData(true, false, true, true)] // typed since, but focus is not in a text box now
    [InlineData(false, false, false, false)] // nothing to take back
    [InlineData(false, true, false, false)]
    public void CtrlZTakesBackTheDeleteUnlessTheTechnicianHasTypedSince(bool pending, bool inTextBox, bool typedSince, bool expected) =>
        Assert.Equal(expected, NoteLanding.UndoIsTheDelete(pending, inTextBox, typedSince));

    [Fact]
    public void NoneLeftToReviewMeansEveryMaskedScreenshotThatGoesOutIsOnAStep()
    {
        // Owner decision (v0.6.8): the wording stays as built. One masked screenshot on a step, one not:
        // the count names the one the note does not show; with both on steps there is none left.
        var both = new HashSet<string>(["f1", "f2"], StringComparer.Ordinal);
        var one = new HashSet<string>(["f1"], StringComparer.Ordinal);

        Assert.Equal("2 items masked, none left to review", NoteLanding.Masking([Frame("f1", 1), Frame("f2", 1)], both));
        Assert.Equal("2 items masked, 1 on a screenshot no step shows", NoteLanding.Masking([Frame("f1", 1), Frame("f2", 1)], one));
    }

    [Fact]
    public void TheCopyHasNoDashes() =>
        Assert.All(
            [NoteLanding.Guide, NoteLanding.Inferred, NoteLanding.EmptySection, NoteLanding.FrameHelp, NoteLanding.FrameTip],
            line => Assert.DoesNotContain("\u2014", line, StringComparison.Ordinal));

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
