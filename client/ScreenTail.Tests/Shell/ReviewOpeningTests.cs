using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// What the shell may ask as a Review pane opens (2026-10-12 review, S-44, S-147, S-45). A local-only
/// session's banner says nothing leaves the device, and the pane must not make a liar of it (INV-8).
/// </summary>
public sealed class ReviewOpeningTests
{
    [Fact]
    public void ALocalOnlySessionDoesNotAskThePsaAsReviewOpens()
    {
        // The suggested ticket was read off the technician's window. Under local-only it waits for the
        // technician to act in the publish pane; it is not sent as a search the moment Review opens.
        Assert.False(ReviewOpening.MayLookUpSuggestedTicket(localOnly: true, suggestedTicket: "48213", integrations: 1));
    }

    [Fact]
    public void AnOrdinarySessionWithAHintAndAPsaLooksTheTicketUp()
    {
        // The control: the badge shows the ticket's summary and company, which is ST-077's point.
        Assert.True(ReviewOpening.MayLookUpSuggestedTicket(localOnly: false, suggestedTicket: "48213", integrations: 1));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("48213", 0)]
    public void NothingToAskAboutOrNobodyToAskMeansNoSearch(string? hint, int integrations)
    {
        Assert.False(ReviewOpening.MayLookUpSuggestedTicket(localOnly: false, hint, integrations));
    }
}
