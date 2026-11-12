using System.Runtime.Versioning;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The note as it will land (Spec v0.6.0 §5 S3): each step row carries the strip's own frame, called what
/// the ticket calls it, with its masks; Space on it is the strip's Space; pressing it opens it enlarged;
/// an inferred step has Looks right; and under the note the masking count follows the strip.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NoteAsItLandsTests
{
    [Fact]
    public void AStepsFrameIsTheStripsOwnAndIsCalledWhatTheTicketCallsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(Draft("Spooler", "f3"), Frames(3)), frames: null);
        var chip = Assert.Single(review.Note.Steps.Single().Frames);

        Assert.Same(review.Strip.Items.OfType<FrameItem>().Single(i => i.Id == "f3"), chip.Item);
        Assert.Equal("screenshot 3", chip.Place);
        Assert.Equal("Step 1 screenshot, frame 3, screenshot 3", chip.SpokenName);
    }

    [Fact]
    public async Task SpaceOnAStepsFrameIsTheStripsSpaceAndThePlaceFollows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var review = new ReviewViewModel(Session(Draft("Spooler", "f3"), Frames(3)), frames);
        var chip = review.Note.Steps.Single().Frames.Single();

        await review.Note.ToggleFrameCommand.ExecuteAsync(chip);

        Assert.Equal([("f3", false)], frames.Included);
        Assert.DoesNotContain("f3", review.Strip.IncludedFrameIds);
        Assert.Equal("not attached", chip.Place);

        // Excluding an earlier frame renumbers what goes out, as the ticket will.
        await review.Note.ToggleFrameCommand.ExecuteAsync(chip);
        await review.Strip.ToggleIncludeCommand.ExecuteAsync(review.Strip.Items.OfType<FrameItem>().First());
        Assert.Equal("screenshot 2", chip.Place);
    }

    [Fact]
    public async Task ADeletedFrameLeavesItsStepAndComesBackWithTheUndo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(Draft("Spooler", "f2"), Frames(2)), new RecordingFrames());
        var chip = review.Note.Steps.Single().Frames.Single();

        await review.Strip.DeleteCommand.ExecuteAsync(chip.Item);
        Assert.False(chip.Present);

        await review.Strip.UndoCommand.ExecuteAsync(null);
        Assert.True(chip.Present);
    }

    [Fact]
    public void PressingAStepsFrameSelectsItInTheStripAndEnlargesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(Draft("Spooler", "f2"), Frames(3)), frames: null);

        review.Note.ShowFrameCommand.Execute("f2");

        Assert.Equal("f2", Assert.IsType<FrameItem>(review.Strip.Selected).Id);
        Assert.Equal("f2", review.Strip.Enlarged?.Id);
        Assert.True(review.Strip.Shown);
        Assert.False(review.Strip.Browsing);
    }

    [Fact]
    public void AMaskedFrameCarriesItsMasksAndTheCountIsUnderTheNote()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var masked = Frame("f1", 1000) with
        {
            MaskedRegions =
            [
                new MaskedRegion { X = 10, Y = 20, Width = 30, Height = 40, Kind = MaskKind.Password },
                new MaskedRegion { X = 100, Y = 200, Width = 50, Height = 10, Kind = MaskKind.Email },
            ],
        };
        var review = new ReviewViewModel(Session(Draft("Spooler", "f1"), masked, Frame("f2", 2000)), frames: null);
        var chip = review.Note.Steps.Single().Frames.Single();

        Assert.Equal(2, chip.Masks.Count);
        Assert.Equal(new System.Windows.Thickness(10, 20, 0, 0), chip.Masks[0].Offset);
        Assert.Equal((30d, 40d), (chip.Masks[0].Width, chip.Masks[0].Height));
        Assert.Equal("2 masked", chip.MaskedLabel);
        Assert.Equal("2 items masked, none left to review", review.Note.MaskingLabel);
    }

    [Fact]
    public async Task ExcludingTheMaskedFrameTakesItsMasksOutOfTheCount()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var masked = Frame("f2", 2000) with { MaskedRegions = [new MaskedRegion { X = 1, Y = 1, Width = 5, Height = 5, Kind = MaskKind.Card }] };
        var review = new ReviewViewModel(Session(Draft("Spooler", "f1"), Frame("f1", 1000), masked), new RecordingFrames());

        // On no step: counted, and said to be on a screenshot no step shows.
        Assert.Equal("1 item masked, 1 on a screenshot no step shows", review.Note.MaskingLabel);

        await review.Strip.ToggleIncludeCommand.ExecuteAsync(review.Strip.Items.OfType<FrameItem>().Single(i => i.Id == "f2"));
        Assert.Equal("Nothing masked", review.Note.MaskingLabel);
    }

    [Fact]
    public void AnInferredStepHasLooksRightNamedForItsStepAndConfirmingClearsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var draft = Draft() with
        {
            Steps =
            [
                new DraftStep { Text = "Checked the spooler.", Confidence = StepConfidence.High, FrameRefs = [] },
                new DraftStep { Text = "Cleared the queue.", Confidence = StepConfidence.Low, FrameRefs = [] },
            ],
        };
        var review = new ReviewViewModel(Session(draft), frames: null);
        var step = review.Note.Steps[1];

        Assert.True(step.NeedsVerification);
        Assert.Equal("Inferred from screen only", step.InferredLine);
        Assert.Equal("Looks right, step 2", step.ConfirmName);
        Assert.Equal("Edit step 2", step.EditName);
        Assert.Equal("2", step.Number);

        review.Note.ConfirmStepCommand.Execute(step);

        Assert.False(step.NeedsVerification);
        Assert.Equal("Every step checked", review.Note.UnverifiedLabel);
    }

    [Fact]
    public void ATranscriptLineMarksTheStepItsFrameIsOn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(Draft("Spooler", "f2"), Frames(2)), frames: null);

        review.Note.Highlight("f2");
        Assert.True(review.Note.Steps.Single().IsCurrent);

        review.Note.Highlight("f1");
        Assert.False(review.Note.Steps.Single().IsCurrent);
    }

    [Fact]
    public void AllScreenshotsOpensTheStripOverTheNote()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(Frames(3)), frames: null);
        Assert.False(review.Strip.Shown);
        Assert.Equal("All screenshots, 3 of 3 included", review.Note.AllScreenshotsName);

        review.Note.ShowAllScreenshotsCommand.Execute(null);

        Assert.True(review.Strip.Browsing);
        Assert.True(review.Strip.Shown);
    }
}
