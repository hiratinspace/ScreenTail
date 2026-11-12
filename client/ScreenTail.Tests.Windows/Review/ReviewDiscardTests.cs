using System.Runtime.Versioning;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The Discard in Review's header (Spec §5 S3, ST-074): the button the header binds to
/// <c>Note.DiscardCommand</c> asks the typed-confirmation dialog for DISCARD and only then calls the
/// discard the shell wired, which asks the service by name (contract v6).
///
/// The dialog and the service are delegates that remember what they were asked, as in
/// <see cref="ReviewPaneTests"/>. That the header's button is bound to this command is held on the Mac,
/// from the XAML (DiscardControlsTests).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReviewDiscardTests
{
    [Fact]
    public void TheHeadersDiscardAsksForDiscardAndThenDiscards()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var asked = new List<TypedConfirmation>();
            var discards = 0;
            await using var review = new ReviewViewModel(
                Session(Frames(2)),
                new RecordingFrames(),
                confirm: confirmation =>
                {
                    asked.Add(confirmation);
                    return true;
                },
                discard: _ =>
                {
                    discards++;
                    return Task.CompletedTask;
                });

            Assert.True(review.Note.DiscardCommand.CanExecute(null));
            await review.Note.DiscardCommand.ExecuteAsync(null);

            var typed = Assert.Single(asked);
            Assert.True(typed.Accepts("DISCARD"));
            Assert.False(typed.Accepts("discard"));
            Assert.Equal("Type DISCARD to confirm", typed.Prompt);
            Assert.Equal(1, discards);
            Assert.False(review.Note.DiscardFailed);
        });
    }

    [Fact]
    public void NotTypingTheWordDiscardsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var discards = 0;
            await using var review = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                confirm: _ => false,
                discard: _ =>
                {
                    discards++;
                    return Task.CompletedTask;
                });

            await review.Note.DiscardCommand.ExecuteAsync(null);

            Assert.Equal(0, discards);
        });
    }

    [Fact]
    public void ASessionStillBeingCapturedSaysWhyInsteadOfAsking()
    {
        // Spec §4: no modal while a session records, and Review of the running session is read-only
        // until it stops (§5 S2). The reason goes beside the button, which a keyboard user can reach.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var asked = 0;
            var discards = 0;
            await using var review = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                confirm: _ =>
                {
                    asked++;
                    return true;
                },
                discard: _ =>
                {
                    discards++;
                    return Task.CompletedTask;
                },
                discardBlocked: () => "This session is still being captured. Stop it first, then discard it.");

            await review.Note.DiscardCommand.ExecuteAsync(null);

            Assert.Equal(0, asked);
            Assert.Equal(0, discards);
            Assert.True(review.Note.DiscardFailed);
            Assert.Equal("This session is still being captured. Stop it first, then discard it.", review.Note.DiscardError);
        });
    }

    [Fact]
    public void WhileTheSessionIsCapturedDiscardIsOffAndSaysWhyBesideIt()
    {
        // The button was greyed out with no reason anyone could see. The reason is beside it now, and goes
        // when capture stops, so the button comes back without the technician leaving Review.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var capturing = true;
            await using var review = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                confirm: _ => true,
                discard: _ => Task.CompletedTask,
                discardBlocked: () => capturing ? "This session is still being captured. Stop it first, then discard it." : null);

            Assert.Equal("Stop the session to discard", review.Note.DiscardHint);
            Assert.True(review.Note.DiscardHintVisible);
            Assert.False(review.Note.DiscardCommand.CanExecute(null));

            capturing = false;
            review.Note.RefreshDiscard();

            Assert.Null(review.Note.DiscardHint);
            Assert.False(review.Note.DiscardHintVisible);
            Assert.True(review.Note.DiscardCommand.CanExecute(null));
        });
    }

    [Fact]
    public void ASessionThatBeginsWhileTheDialogIsUpStopsTheDiscardAndTheNoteGoesOnSaving()
    {
        // As the mark and History's bulk discard: the service starts sessions by itself, so whether one is
        // being captured is asked again once DISCARD is typed. Nothing is sent, the reason goes beside the
        // button, and the note's autosave was never stopped.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            const string Reason = "A session is being captured. Stop it first, then discard this one.";
            var recording = false;
            var discards = 0;
            var written = new List<DraftNote>();
            await using var review = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                write: (draft, _) =>
                {
                    written.Add(draft);
                    return Task.CompletedTask;
                },
                confirm: _ =>
                {
                    recording = true;
                    return true;
                },
                discard: _ =>
                {
                    discards++;
                    return Task.CompletedTask;
                },
                discardBlocked: () => recording ? Reason : null);

            await review.Note.DiscardCommand.ExecuteAsync(null);

            Assert.Equal(0, discards);
            Assert.True(review.Note.DiscardFailed);
            Assert.Equal(Reason, review.Note.DiscardError);

            review.Note.Problem = "Typed while the session records";
            await review.Note.SaveNowCommand.ExecuteAsync(null);
            Assert.Equal("Typed while the session records", Assert.Single(written).Problem);
        });
    }

    [Fact]
    public void ARefusedDiscardSaysWhyAndTheNoteGoesOnSaving()
    {
        // The note's autosave stops before the discard is sent, so a write landing afterwards cannot put
        // the note back into a discarded session. When the service refuses, the session is still there
        // and still the technician's, and what they type next has to be kept.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var written = new List<DraftNote>();
            await using var review = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                write: (draft, _) =>
                {
                    written.Add(draft);
                    return Task.CompletedTask;
                },
                confirm: _ => true,
                discard: _ => throw new InvalidOperationException("Nothing was discarded. 1 session was already discarded."));

            await review.Note.DiscardCommand.ExecuteAsync(null);

            Assert.True(review.Note.DiscardFailed);
            Assert.Equal("Nothing was discarded. 1 session was already discarded.", review.Note.DiscardError);

            review.Note.Problem = "Typed after the refusal";
            await review.Note.SaveNowCommand.ExecuteAsync(null);
            Assert.Equal("Typed after the refusal", Assert.Single(written).Problem);
        });
    }

    [Fact]
    public void WhileTheDialogIsUpASecondInvokeOpensNoSecondDialog()
    {
        // Review of #274. The dialog runs a nested message loop before the command's first await, where
        // the command does not yet count as running, so a queued invoke arriving there opened another.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var asked = 0;
            var discards = 0;
            var offDuringDialog = false;
            ReviewViewModel? review = null;
            await using var model = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                confirm: _ =>
                {
                    asked++;
                    offDuringDialog = !review!.Note.DiscardCommand.CanExecute(null);
                    review.Note.DiscardCommand.Execute(null);
                    return true;
                },
                discard: _ =>
                {
                    discards++;
                    return Task.CompletedTask;
                });
            review = model;

            await model.Note.DiscardCommand.ExecuteAsync(null);

            Assert.Equal(1, asked);
            Assert.Equal(1, discards);
            Assert.True(offDuringDialog);
        });
    }

    [Fact]
    public void WhileTheDiscardIsOnItsWayTheButtonIsOffAndASecondPressAsksNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var held = new TaskCompletionSource();
            var asked = 0;
            await using var review = new ReviewViewModel(
                Session(Frames(1)),
                new RecordingFrames(),
                confirm: _ =>
                {
                    asked++;
                    return true;
                },
                discard: _ => held.Task);

            var first = review.Note.DiscardCommand.ExecuteAsync(null);

            Assert.True(review.Note.DiscardCommand.IsRunning);
            Assert.False(review.Note.DiscardCommand.CanExecute(null));
            review.Note.DiscardCommand.Execute(null);
            Assert.False(review.Note.DiscardCommand.CanExecute(null));
            Assert.Equal(1, asked);

            held.SetResult();
            await first;

            Assert.Equal(1, asked);
            Assert.True(review.Note.DiscardCommand.CanExecute(null));
        });
    }

    [Fact]
    public void WithNothingWiredTheButtonIsDisabled()
    {
        // The render harness has no service. A Discard that looks pressable and does nothing is a lie
        // about the one irreversible action on the screen.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            await using var review = new ReviewViewModel(Session(Frames(1)), frames: null);

            Assert.False(review.Note.DiscardCommand.CanExecute(null));
        });
    }

    [Fact]
    public void TheHeaderNamesTheSession()
    {
        // Spec v0.6.0 §5 S3: the envelope.s subject line is the note.s title, or plain Review before there is a note.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            await using var drafted = new ReviewViewModel(Session(Frames(1)), frames: null);
            await using var failed = new ReviewViewModel(Session(draft: null, Frames(1)), frames: null);

            Assert.Equal("Printer offline", drafted.Title);
            Assert.Equal("Review", failed.Title);
        });
    }
}
