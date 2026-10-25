using System.Runtime.Versioning;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The links between the three panes that <see cref="ReviewViewModel"/> wires (ST-075; 2026-10-12 review
/// T-172, T-165, T-169, E-18): what a publish carries, a note's frame chip reaching the strip, and what
/// leaving the screen writes. The service and the PSA are delegates that remember what they were told.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReviewPaneTests
{
    private static readonly string[] ConnectWise = ["connectwise"];

    [Fact]
    public async Task PublishSendsTheEditedNoteAndOnlyTheFramesTheStripIncludes()
    {
        // T-172. Two lambdas in ReviewViewModel are all that keep the pre-edit draft, an excluded frame
        // and a frame inside its undo window out of a publish. The service trusts that list.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var sent = new List<PublishRequest>();
        var session = Session(Frames(4));
        var review = new ReviewViewModel(session, new RecordingFrames(), publish: Panel(session, sent));
        review.Publish.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");
        var items = review.Strip.Items.OfType<FrameItem>().ToList();

        review.Note.Problem = "Edited in Review";
        await review.Strip.ToggleIncludeCommand.ExecuteAsync(items[1]);
        await review.Strip.DeleteCommand.ExecuteAsync(items[2]);
        Assert.True(review.Publish.CanPublish);

        await review.Publish.PublishCommand.ExecuteAsync(null);

        var request = Assert.Single(sent);
        Assert.Equal("Edited in Review", request.Note.Problem);
        Assert.NotEqual(session.Draft!.Problem, request.Note.Problem);
        Assert.Equal(["f1", "f4"], request.FrameIds);
        Assert.Equal("48213", request.Ticket.Id);
    }

    [Fact]
    public async Task TooManyIncludedFramesBlockPublishUntilSomeAreExcluded()
    {
        // E-18. The backend takes 25 frames and the strip includes every frame by default. A 26-frame
        // session used to read, encode and upload everything for a certain refusal; the pane says so
        // first, and Ctrl+Enter is refused the same way as the button.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var sent = new List<PublishRequest>();
        var session = Session(Frames(PublishCommands.MaxFrames + 1));
        var review = new ReviewViewModel(session, new RecordingFrames(), publish: Panel(session, sent));
        review.Publish.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");

        Assert.False(review.Publish.CanPublish);
        Assert.Contains($"{PublishCommands.MaxFrames}", review.Publish.BlockedBecause, StringComparison.Ordinal);
        Assert.Contains($"{PublishCommands.MaxFrames + 1}", review.Publish.BlockedBecause, StringComparison.Ordinal);
        await review.Publish.PublishCommand.ExecuteAsync(null);
        Assert.Empty(sent);

        await review.Strip.ToggleIncludeCommand.ExecuteAsync(review.Strip.Items.OfType<FrameItem>().Last());

        Assert.True(review.Publish.CanPublish);
        Assert.Equal(string.Empty, review.Publish.BlockedBecause);
        await review.Publish.PublishCommand.ExecuteAsync(null);
        Assert.Equal(PublishCommands.MaxFrames, Assert.Single(sent).FrameIds.Count);
    }

    [Fact]
    public void ClickingAFrameChipSelectsThatFrameInTheStrip()
    {
        // T-165. The chip under a step is the note's way into the filmstrip. It was once raised and never
        // heard; now it is a command the note's view model hands to the strip, the way the timeline's
        // transcript lines are, so the link is in the model and not only in XAML.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(Draft("Spooler", "f2"), Frames(3)), frames: null);
        var chip = Assert.Single(review.Note.Steps.Single().Frames);
        Assert.Equal("frame 2", chip.Label);

        review.Note.ShowFrameCommand.Execute(chip.Id);

        Assert.Equal("f2", Assert.IsType<FrameItem>(review.Strip.Selected).Id);

        review.Note.ShowFrameCommand.Execute("nope");
        Assert.Null(review.Strip.Selected);
    }

    [Fact]
    public async Task LeavingReviewWritesWhatWasTypedInTheLastSecond()
    {
        // T-169. The autosave waits 400 ms of quiet before it writes, so navigating away or closing the
        // window right after typing lost that much. NoteEditorViewModel.DisposeAsync flushed it, and
        // nothing called it. Disposing the screen does, and a pending delete in the strip is committed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var written = new List<DraftNote>();
        var frames = new RecordingFrames();
        var review = new ReviewViewModel(Session(Frames(2)), frames, write: (draft, _) => { written.Add(draft); return Task.CompletedTask; });
        await review.Strip.DeleteCommand.ExecuteAsync(review.Strip.Items.OfType<FrameItem>().First());

        review.Note.Problem = "Typed just before leaving";
        Assert.Empty(written);

        await review.DisposeAsync();

        Assert.Equal("Typed just before leaving", Assert.Single(written).Problem);
        Assert.Equal(["f1"], frames.Deleted);

        // Twice is fine: the view's Unloaded and the shell's close can both get there.
        await review.DisposeAsync();
        Assert.Single(written);
        Assert.Equal(["f1"], frames.Deleted);
    }

    private static PublishPanel Panel(Session session, List<PublishRequest> sent) => new(
        session,
        ConnectWise,
        (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
        (request, _) =>
        {
            sent.Add(request);
            return Task.FromResult<IReadOnlyList<DestinationResult>>([.. request.Destinations.Select(d => new DestinationResult(d, true, new Uri("https://na.myconnectwise.net/t/48213")))]);
        });
}
