using System.Text.Json;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>ST-074: the note pane's editing rules (Spec §5 S3, left pane).</summary>
public sealed class NoteDraftTests
{
    [Fact]
    public void ALowConfidenceStepIsMarkedUntilSomebodyChecksIt()
    {
        var note = new NoteDraft(Draft(Step("Restarted the spooler.", StepConfidence.Low)));

        Assert.True(note.Steps[0].NeedsVerification);
        Assert.Equal(1, note.UnverifiedSteps);

        note.ConfirmStep(note.Steps[0].Id);

        Assert.False(note.Steps[0].NeedsVerification);
        Assert.Equal(0, note.UnverifiedSteps);
    }

    [Fact]
    public void EditingAStepCountsAsCheckingIt()
    {
        // Spec: the marker clears on edit or on Alt+C. A technician who rewrote the sentence has already
        // done the verifying the marker was asking for, and leaving it up would be asking twice.
        var note = new NoteDraft(Draft(Step("Restarted the spooler.", StepConfidence.Low)));

        note.SetStepText(note.Steps[0].Id, "Restarted the Print Spooler service from services.msc.");

        Assert.False(note.Steps[0].NeedsVerification);
    }

    [Fact]
    public void ConfirmingDoesNotRewriteWhatTheDraftClaimed()
    {
        // The tempting shortcut is to set confidence to high and be done. It would erase the record of how
        // the step was arrived at - which is the thing ST-062 measures the model against - in order to
        // record something else entirely.
        var note = new NoteDraft(Draft(Step("Restarted the spooler.", StepConfidence.Low)));
        note.ConfirmStep(note.Steps[0].Id);

        var saved = note.ToSchema().Steps[0];

        Assert.Equal(StepConfidence.Low, saved.Confidence);
        Assert.True(saved.Confirmed);
    }

    [Fact]
    public void AConfirmedStepStaysConfirmedAfterARestart()
    {
        // The AC says edits survive a restart. Confirming six steps and finding all six marked again on
        // reopening is the same failure wearing a different hat.
        var note = new NoteDraft(Draft(Step("Restarted the spooler.", StepConfidence.Low)));
        note.ConfirmStep(note.Steps[0].Id);

        var reopened = new NoteDraft(note.ToSchema());

        Assert.False(reopened.Steps[0].NeedsVerification);
    }

    [Fact]
    public void AnUntouchedNoteSerialisesExactlyAsItArrived()
    {
        // Nothing the editor does on load may show up as an edit, or every note ever opened would differ
        // from the draft that produced it and the fixtures would all gain a field the model never sets.
        // Compared as JSON because that is the claim: record equality on DraftNote compares the list
        // references, so it passes whatever the lists contain.
        var draft = Draft(Step("Checked the spooler service.", StepConfidence.High, "f-0002"));

        var written = new NoteDraft(draft).ToSchema();

        Assert.Null(written.Steps[0].Confirmed);
        Assert.Equal(JsonSerializer.Serialize(draft), JsonSerializer.Serialize(written));
    }

    [Fact]
    public void AStepTheTechnicianTypedIsNeverMarkedInferred()
    {
        var note = new NoteDraft(Draft());

        var added = note.InsertStepAfter(note.Steps[0].Id);

        Assert.False(added.NeedsVerification);
        Assert.Equal(StepConfidence.High, added.Confidence);
    }

    [Fact]
    public void AnEmptyStepStaysInTheEditorAndOutOfTheFile()
    {
        // The schema requires text, so a document holding an empty step cannot be saved at all. Refusing
        // the whole save would mean a technician who pressed Enter and then went to lunch loses every
        // other edit too.
        var note = new NoteDraft(Draft());
        note.InsertStepAfter(note.Steps[0].Id);

        Assert.Equal(2, note.Steps.Count);
        Assert.Single(note.ToSchema().Steps);
    }

    [Fact]
    public void TheEmptyStepIsWrittenAsSoonAsItSaysSomething()
    {
        var note = new NoteDraft(Draft());
        var added = note.InsertStepAfter(note.Steps[0].Id);

        note.SetStepText(added.Id, "Cleared the print queue.");

        Assert.Equal(2, note.ToSchema().Steps.Count);
        Assert.Equal("Cleared the print queue.", note.ToSchema().Steps[1].Text);
    }

    [Fact]
    public void TheLastStepCannotBeDeleted()
    {
        // Backspace on the only step would leave a Steps heading with nothing under it and nowhere for the
        // technician's caret to be, and the next keystroke would go somewhere they did not choose.
        var note = new NoteDraft(Draft());

        Assert.False(note.DeleteStep(note.Steps[0].Id));
        Assert.Single(note.Steps);
    }

    [Fact]
    public void DeletingReportsWhetherItHappened()
    {
        // The view moves the caret to the step above, and only when there is one fewer step than before.
        var note = new NoteDraft(Draft(Step("one"), Step("two")));

        Assert.True(note.DeleteStep(note.Steps[1].Id));
        Assert.False(note.DeleteStep("nonexistent"));
        Assert.Single(note.Steps);
    }

    [Fact]
    public void ANoteThatComesBackWithNoStepsCanStillBeTypedIn()
    {
        // Clearing the only step's text writes steps: [], because ToSchema drops empty ones. On reopening,
        // a Steps heading with nothing under it has nowhere for the caret to go, and the only way to make
        // a step is to press Enter inside one - so the note would be unwritable for good.
        var note = new NoteDraft(Draft());
        note.SetStepText(note.Steps[0].Id, "   ");

        var written = note.ToSchema();
        Assert.Empty(written.Steps);

        var reopened = new NoteDraft(written);

        Assert.Single(reopened.Steps);
        Assert.Equal(string.Empty, reopened.Steps[0].Text);
    }

    [Fact]
    public void AStaleEnterAppendsRatherThanJumpingToTheTop()
    {
        // IndexOf returns -1 for an id that is gone, and the first version added 1 to it - so a keystroke
        // about a deleted step put the new one at the top of the note. Every other method here ignores an
        // unknown id; this one silently reordered the note.
        var note = new NoteDraft(Draft(Step("one"), Step("two")));

        var added = note.InsertStepAfter("nonexistent");

        Assert.Equal(added.Id, note.Steps[^1].Id);
        Assert.Equal(["one", "two"], note.Steps.Take(2).Select(step => step.Text));
    }

    [Fact]
    public void ReorderingMovesTheStepAndNothingElse()
    {
        var note = new NoteDraft(Draft(Step("one"), Step("two"), Step("three")));
        var second = note.Steps[1].Id;

        Assert.True(note.MoveStep(second, -1));

        Assert.Equal(["two", "one", "three"], note.Steps.Select(step => step.Text));
        Assert.Equal(second, note.Steps[0].Id);
    }

    [Fact]
    public void ReorderingStopsAtEitherEndInsteadOfWrapping()
    {
        // Alt+↑ on the first step must do nothing visible. Returning false is how the pane knows to leave
        // the focus where it is rather than appearing to have acted.
        var note = new NoteDraft(Draft(Step("one"), Step("two")));
        var before = note.Revision;

        Assert.False(note.MoveStep(note.Steps[0].Id, -1));
        Assert.False(note.MoveStep(note.Steps[1].Id, 1));
        Assert.Equal(before, note.Revision);
    }

    [Fact]
    public void AKeystrokeAboutAStepThatIsGoneIsIgnored()
    {
        // Every one of these is reached from a keyboard handler, where the step may have been deleted
        // between the key going down and the handler running.
        var note = new NoteDraft(Draft());
        var before = note.Revision;

        note.SetStepText("nonexistent", "hello");
        note.ConfirmStep("nonexistent");
        note.DeleteStep("nonexistent");

        Assert.False(note.MoveStep("nonexistent", 1));
        Assert.Equal(before, note.Revision);
    }

    [Fact]
    public void RetypingTheSameTextIsNotAnEdit()
    {
        // WPF raises TextChanged for things that are not changes. Counting them would keep the autosave
        // writing forever and the indicator saying "Saving…" at a technician who has stopped typing.
        var note = new NoteDraft(Draft());
        var text = note.Steps[0].Text;
        var before = note.Revision;

        note.SetStepText(note.Steps[0].Id, text);
        note.SetProblem(note.Problem);
        note.SetResult(note.Result);

        Assert.Equal(before, note.Revision);
    }

    [Fact]
    public void EveryAcceptedChangeIsAnnouncedExactlyOnce()
    {
        var note = new NoteDraft(Draft());
        var changes = 0;
        note.Changed += () => changes++;

        note.SetProblem("Something else.");
        note.SetProblem("Something else.");
        note.SetResult("Fixed.");

        Assert.Equal(2, changes);
        Assert.Equal(2, note.Revision);
    }

    [Fact]
    public void BlankFollowUpLinesAreNotBullets()
    {
        var note = new NoteDraft(Draft());

        note.SetFollowUpsText("Replace the toner.\r\n\n  Order a spare drum.  \n");

        Assert.Equal(["Replace the toner.", "Order a spare drum."], note.FollowUps);
        Assert.Equal("Replace the toner.\nOrder a spare drum.", note.FollowUpsText());
    }

    [Fact]
    public void EditingFollowUpsToTheSameListIsNotAnEdit()
    {
        var note = new NoteDraft(Draft());
        note.SetFollowUpsText("Replace the toner.");
        var before = note.Revision;

        note.SetFollowUpsText("Replace the toner.\n\n");

        Assert.Equal(before, note.Revision);
    }

    [Fact]
    public void TheFramesAStepCitesAreCarriedThroughUnchanged()
    {
        // The chips are how the note points at its evidence. Losing them on save would leave a published
        // note whose steps cite nothing.
        var note = new NoteDraft(Draft(Step("Checked the spooler.", StepConfidence.High, "f-0002", "f-0003")));

        note.SetStepText(note.Steps[0].Id, "Checked the spooler service.");

        Assert.Equal(["f-0002", "f-0003"], note.ToSchema().Steps[0].FrameRefs);
    }
}
