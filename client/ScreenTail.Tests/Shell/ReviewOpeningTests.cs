using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// What the shell may ask as a Review pane opens (2026-10-12 review, S-44, S-147, S-45). A local-only
/// session's banner says nothing leaves the device, and the pane must not make a liar of it (INV-8).
/// Local-only is also a setting in force now: a session recorded before the technician (or their admin)
/// turned it on is opened under it, and the lookup is egress all the same.
/// </summary>
public sealed class ReviewOpeningTests
{
    [Fact]
    public async Task ALocalOnlySessionDoesNotAskThePsaAsReviewOpens()
    {
        // The suggested ticket was read off the technician's window. Under local-only it waits for the
        // technician to act in the publish pane; it is not sent as a search the moment Review opens.
        Assert.False(await ReviewOpening.MayLookUpSuggestedTicketAsync(sessionLocalOnly: true, "48213", integrations: 1, Now(false)));
    }

    [Fact]
    public async Task LocalOnlyInForceNowStopsTheLookUpForAnOlderSession()
    {
        // The session was recorded before local-only was turned on. Opening it now must not search the
        // PSA: the setting says nothing leaves the device from here on, whatever the session's own flag.
        Assert.False(await ReviewOpening.MayLookUpSuggestedTicketAsync(sessionLocalOnly: false, "48213", integrations: 1, Now(true)));
    }

    [Fact]
    public async Task ASettingTheServiceWouldNotReportStopsTheLookUp()
    {
        // Fail closed: when the service does not say whether local-only is on, the pane does not guess
        // that it is off. The technician's own search in the publish pane still works.
        Assert.False(await ReviewOpening.MayLookUpSuggestedTicketAsync(sessionLocalOnly: false, "48213", integrations: 1, Now(null)));
    }

    [Fact]
    public async Task AnOrdinarySessionWithAHintAndAPsaLooksTheTicketUp()
    {
        // The control: the badge shows the ticket's summary and company, which is ST-077's point.
        Assert.True(await ReviewOpening.MayLookUpSuggestedTicketAsync(sessionLocalOnly: false, "48213", integrations: 1, Now(false)));
    }

    [Theory]
    [InlineData(false, null, 1)]
    [InlineData(false, "", 1)]
    [InlineData(false, "   ", 1)]
    [InlineData(false, "48213", 0)]
    [InlineData(true, "48213", 1)]
    public async Task NothingToAskAboutOrNobodyToAskMeansNoSearchAndNoQuestion(bool sessionLocalOnly, string? hint, int integrations)
    {
        // Nothing would be looked up, so the service is not asked for the setting either: opening Review
        // costs no extra round trip when there is no lookup to decide.
        var asked = 0;

        var may = await ReviewOpening.MayLookUpSuggestedTicketAsync(sessionLocalOnly, hint, integrations, _ =>
        {
            asked++;
            return Task.FromResult<bool?>(false);
        });

        Assert.False(may);
        Assert.Equal(0, asked);
    }

    private static Func<CancellationToken, Task<bool?>> Now(bool? localOnly) => _ => Task.FromResult(localOnly);
}
