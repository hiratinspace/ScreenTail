using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>ST-074: what the note pane says across the top before somebody publishes (Spec §5 S3).</summary>
public sealed class NoteBannerTests
{
    [Fact]
    public void ACleanSessionSaysNothing()
    {
        // A bar that is always there is a bar nobody reads, including on the day it says something.
        Assert.Empty(NoteBanners.For(Session(draft: Draft())));
    }

    [Fact]
    public void PartialCaptureAndPurgedFramesAreBothSaid()
    {
        var banners = NoteBanners.For(Session(partialCapture: true, framesPurged: 3, draft: Draft()));

        Assert.Equal(
            [
                "Capture started late or was interrupted. Some steps may be missing.",
                "3 screenshots were removed because they could not be redacted in time.",
            ],
            banners.Select(banner => banner.Text));
        Assert.All(banners, banner => Assert.Equal(BannerKind.Warning, banner.Kind));
    }

    [Fact]
    public void OneRemovedScreenshotIsSaidInTheSingular()
    {
        // "1 screenshots were removed" reads as a bug and invites the reader to distrust the count, which
        // is the one number on this screen that has to be believed.
        var banners = NoteBanners.For(Session(framesPurged: 1, draft: Draft()));

        Assert.Equal("1 screenshot was removed because they could not be redacted in time.", banners[0].Text);
    }

    [Fact]
    public void ALocalDraftIsSaidOnceNotTwice()
    {
        // Local-only mode is why the draft is local, so saying both is saying the same thing in two
        // sentences and pushing whatever else is wrong further down the pane.
        var local = Draft() with { Source = DraftSource.Local };

        var banners = NoteBanners.For(Session(localOnly: true, draft: local));

        Assert.Single(banners);
        Assert.Equal("Drafted on this device only — quality may be lower.", banners[0].Text);
    }

    [Fact]
    public void LocalOnlyWithNoDraftYetStillExplainsItself()
    {
        var banners = NoteBanners.For(Session(localOnly: true));

        Assert.Contains(banners, banner => banner.Text.StartsWith("Local-only mode", StringComparison.Ordinal));
    }

    [Fact]
    public void OfflineWithNoDraftSaysTheDraftIsPending()
    {
        Assert.Contains(NoteBanners.For(Session(), offline: true), banner => banner.Text == "Draft pending — offline.");
    }

    [Fact]
    public void OfflineWithADraftAlreadyInHandSaysNothingAboutDrafting()
    {
        // The draft arrived. Being offline now affects publishing, which the right pane says, and repeating
        // it here would tell a technician their note is at risk when it is sitting in front of them.
        Assert.Empty(NoteBanners.For(Session(draft: Draft()), offline: true));
    }

    [Fact]
    public void PublishSaysWhyItIsDisabled()
    {
        // Spec puts the reason next to the button rather than in a tooltip: a tooltip on a disabled control
        // is the one place a keyboard user can never reach it.
        Assert.Equal(
            "There is no note to publish yet. Retry the draft first.",
            NoteBanners.PublishBlockedBecause(Session(), ticketChosen: true));

        Assert.Equal(
            "Choose a ticket first",
            NoteBanners.PublishBlockedBecause(Session(draft: Draft()), ticketChosen: false));

        Assert.Equal(
            "You are offline. Publishing needs a connection.",
            NoteBanners.PublishBlockedBecause(Session(draft: Draft()), ticketChosen: true, offline: true));

        Assert.Null(NoteBanners.PublishBlockedBecause(Session(draft: Draft()), ticketChosen: true));
    }

    [Fact]
    public void AFailedDraftOfflineDoesNotTellTheTechnicianToRetry()
    {
        // Retrying a cloud draft with no connection fails again. The reason has to be the one they can act
        // on, which offline is not.
        Assert.Equal(
            "There is no note to publish yet. It will be drafted when you are back online.",
            NoteBanners.PublishBlockedBecause(Session(), ticketChosen: true, offline: true));
    }
}
