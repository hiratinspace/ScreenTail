using ScreenTail.Core.Intel;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The right pane's rules (ST-078, Spec §5 S3): what it takes to be allowed to publish, what the time
/// entry defaults to, and what happens when a destination fails. The PSA itself is a delegate; ST-092
/// and ST-093 supply the real ones and nothing here changes when they do.
/// </summary>
public sealed class PublishPanelTests
{
    private static readonly string[] ConnectWise = ["connectwise"];

    [Fact]
    public async Task FewerThanThreeCharactersDoesNotSearch()
    {
        // Two characters match half the tickets in a PSA, and each search is a round trip to it.
        var searches = new List<string>();
        var panel = Panel(search: q => { searches.Add(q); return []; });

        Assert.False(await panel.SearchAsync("pr"));
        Assert.True(await panel.SearchAsync("pri"));

        Assert.Equal(["pri"], searches);
    }

    [Fact]
    public async Task ANumberSearchesAtOnceBecauseItIsAnId()
    {
        // ST-092: numeric means "this ticket". A technician who typed 48 is not asking for a keyword.
        var searches = new List<string>();
        var panel = Panel(search: q => { searches.Add(q); return []; });

        Assert.True(await panel.SearchAsync("48"));

        Assert.Equal(["48"], searches);
    }

    [Fact]
    public async Task AMatchIsShownAsIdSummaryCompany()
    {
        var panel = Panel(search: _ => [new TicketMatch("48213", "Printer offline", "Acme Dental")]);

        await panel.SearchAsync("printer");

        Assert.Equal("#48213 · Printer offline · Acme Dental", Assert.Single(panel.Matches).Label);
    }

    [Fact]
    public void WithoutATicketPublishIsBlockedAndSaysWhy()
    {
        var panel = Panel();

        Assert.False(panel.CanPublish);
        Assert.Equal("Choose a ticket first", panel.BlockedBecause);
    }

    [Fact]
    public void ChoosingATicketUnblocksIt()
    {
        var panel = Panel();

        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        Assert.True(panel.CanPublish);
        Assert.Null(panel.BlockedBecause);
    }

    [Fact]
    public void ASuggestedTicketIsChosenAndSaysItWasSuggested()
    {
        // ST-077 pre-selects from the window title; the badge is what tells the technician to check it.
        var panel = Panel();

        panel.Suggest(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        Assert.True(panel.Ticket!.Suggested);
        Assert.True(panel.CanPublish);
    }

    [Fact]
    public void WithNoIntegrationPublishIsReplacedByConnectAPsa()
    {
        var panel = Panel(integrations: []);
        panel.Choose(new TicketMatch("1", "x", "y"));

        Assert.False(panel.HasIntegrations);
        Assert.Equal("Connect a PSA to publish", panel.BlockedBecause);
        Assert.False(panel.CanPublish);
    }

    [Fact]
    public void AFailedDraftHasNothingToPublish()
    {
        var panel = Panel(session: Session(draft: null));
        panel.Choose(new TicketMatch("1", "x", "y"));

        Assert.Equal("There is no note to publish yet. Retry the draft first.", panel.BlockedBecause);
    }

    [Fact]
    public void NoDestinationIsAlsoNothingToPublish()
    {
        var panel = Panel();
        panel.Choose(new TicketMatch("1", "x", "y"));
        panel.TicketNote = false;
        panel.TimeEntry = false;
        panel.KbArticle = false;

        Assert.Equal("Choose at least one destination", panel.BlockedBecause);
    }

    [Fact]
    public void TheTimeEntryStartsFromTheDraftRoundedTheTenantsWay()
    {
        // The draft reports unrounded active minutes and the client applies the tenant's rounding
        // (docs/STATUS.md §4): 23 minutes on a 15-minute increment is a 30-minute entry.
        var panel = Panel(
            session: Session(draft: Draft() with { SuggestedTimeMinutes = 23 }),
            rounding: new TimeEntryOptions { IncrementMinutes = 15 });

        Assert.Equal(30, panel.Minutes);
        Assert.Equal("rounded to 15", panel.RoundingNote);
        Assert.Equal(NoteType.Internal, panel.NoteType);
    }

    [Fact]
    public void TheKbToggleFollowsTheDraftsOpinionAndSaysWhy()
    {
        var candidate = Panel(session: Session(draft: Draft() with { KbCandidate = true, KbReason = "Recurring across three clients" }));
        var not = Panel(session: Session(draft: Draft() with { KbCandidate = false, KbReason = "Not a KB candidate: one-off fix" }));

        Assert.True(candidate.KbArticle);
        Assert.Equal("Recurring across three clients", candidate.KbReason);
        Assert.False(not.KbArticle);
        Assert.Equal("Not a KB candidate: one-off fix", not.KbReason);
    }

    [Fact]
    public async Task ASuccessfulPublishRecordsEveryDestinationWithItsLink()
    {
        PublishRequest? sent = null;
        var panel = Panel(publish: request =>
        {
            sent = request;
            return [.. request.Destinations.Select(d => new DestinationResult(d, true, new Uri($"https://cw.example/{d}")))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        await panel.PublishAsync(Draft(), ["f1", "f2"]);

        Assert.True(panel.Published);
        Assert.False(panel.PartiallyPublished);
        Assert.Equal(["f1", "f2"], sent!.FrameIds);
        Assert.Equal("48213", sent.Ticket.Id);
        Assert.Equal(2, panel.Results.Count);
        Assert.All(panel.Results, r => Assert.True(r.Ok));
    }

    [Fact]
    public async Task APartialFailureKeepsTheSuccessesAndRetriesOnlyWhatFailed()
    {
        // Spec §6: "Note published. Time entry failed: <reason>." with Retry. The note must not be
        // published twice by the retry, which is ST-094's third criterion seen from this side.
        var calls = new List<IReadOnlySet<Destination>>();
        var failTime = true;
        var panel = Panel(publish: request =>
        {
            calls.Add(request.Destinations);
            return [.. request.Destinations.Select(d => d == Destination.TimeEntry && failTime
                ? new DestinationResult(d, false, null, "ConnectWise refused the member")
                : new DestinationResult(d, true, new Uri("https://cw.example/note")))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        await panel.PublishAsync(Draft(), []);
        Assert.True(panel.PartiallyPublished);
        Assert.False(panel.Published);
        Assert.Equal([Destination.TimeEntry], panel.Failed);
        Assert.Equal("Note published. Time entry failed: ConnectWise refused the member", panel.Summary);

        failTime = false;
        await panel.RetryAsync(Draft(), []);

        Assert.True(panel.Published);
        Assert.Equal([Destination.TimeEntry], calls[1]);
        Assert.Equal(2, panel.Results.Count);
        Assert.Equal("Published to ticket #48213 · 0:30 logged", panel.Summary);
    }

    [Fact]
    public async Task PublishingWhileBlockedIsRefusedNotAttempted()
    {
        var attempted = false;
        var panel = Panel(publish: _ => { attempted = true; return []; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));

        Assert.False(attempted);
    }

    private static PublishPanel Panel(
        Session? session = null,
        IReadOnlyList<string>? integrations = null,
        Func<string, IReadOnlyList<TicketMatch>>? search = null,
        Func<PublishRequest, IReadOnlyList<DestinationResult>>? publish = null,
        TimeEntryOptions? rounding = null) => new(
            session ?? Session(draft: Draft()),
            integrations ?? ConnectWise,
            (q, _) => Task.FromResult(search?.Invoke(q) ?? []),
            (r, _) => Task.FromResult(publish?.Invoke(r) ?? [.. r.Destinations.Select(d => new DestinationResult(d, true))]),
            rounding);
}
