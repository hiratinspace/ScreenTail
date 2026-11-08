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
    public async Task FocusWithNothingTypedShowsTheRecentTickets()
    {
        // Spec §5 S3: recent tickets on focus. Asked as a search for nothing, which the service and the
        // backend read as "the recent ones"; the pane never learns what recent means to the PSA.
        var searches = new List<string>();
        var panel = Panel(search: q => { searches.Add(q); return [new TicketMatch("48213", "Printer offline", "Acme Dental")]; });

        Assert.True(await panel.RecentAsync());

        Assert.Equal([string.Empty], searches);
        Assert.Equal("48213", Assert.Single(panel.Matches).Id);
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
    public void TheDevelopmentDemoPsaIsNamedAbovePublishSoNobodyTakesItForConnectWise()
    {
        // ST-090. The backend lists only the demo while it is on; the pane says so where the technician
        // is looking, and Publish works as it would against a real PSA.
        var panel = Panel(integrations: ["demo-psa", "demo-kb"]);
        panel.Choose(new TicketMatch("48213", "Printer offline in reception", "Acme Dental"));

        Assert.True(panel.CanPublish);
        Assert.StartsWith("Publishing to Demo PSA (not ConnectWise)", panel.DemoNotice, StringComparison.Ordinal);
        Assert.Contains("no real ticket", panel.DemoNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealPsaHasNoDemoNotice()
    {
        Assert.Null(Panel(integrations: ConnectWise).DemoNotice);
        Assert.Null(Panel(integrations: []).DemoNotice);
    }

    [Fact]
    public void TheDiscussionHintNamesConnectWiseOnlyWhenTheDemoIsNotWhatPublishReaches()
    {
        // Review 2026-10-27: the line under the note type said ConnectWise during the demo.
        Assert.Equal("Discussion notes are visible to the customer in ConnectWise.", Panel(integrations: ConnectWise).DiscussionHint);
        Assert.Equal("Discussion notes are visible to the customer in ConnectWise.", Panel(integrations: []).DiscussionHint);

        var demo = Panel(integrations: ["demo-psa", "demo-kb"]).DiscussionHint;
        Assert.Equal("Discussion notes would be visible to the customer in a real PSA.", demo);
        Assert.DoesNotContain("ConnectWise", demo, StringComparison.Ordinal);
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
    public async Task ANoteThatLandedWithoutItsScreenshotsIsNotSentAgain()
    {
        // The backend says the note is on the ticket and a screenshot is not (kind "partial"). It is a
        // success, so Retry sends only the time entry, and the line says which screenshot is missing
        // instead of hiding it behind "Published" (2026-10-12 review, S-69 and E-39).
        const string Missing = "The note was published as 90001, but 1 of 2 screenshots did not attach (screenshot-2.jpg): ConnectWise did not answer. The note stays as it is; add the missing screenshots to the ticket by hand rather than publishing the note again.";
        var calls = new List<IReadOnlySet<Destination>>();
        var failTime = true;
        var panel = Panel(publish: request =>
        {
            calls.Add(request.Destinations);
            return [.. request.Destinations.Select(d => d switch
            {
                Destination.TicketNote => new DestinationResult(d, true, new Uri("https://cw.example/note"), Missing, DestinationResult.Partial),
                _ when failTime => new DestinationResult(d, false, null, "ConnectWise refused the member"),
                _ => new DestinationResult(d, true),
            })];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        await panel.PublishAsync(Draft(), []);
        Assert.Equal([Destination.TimeEntry], panel.Failed);
        Assert.Equal($"Note published. Time entry failed: ConnectWise refused the member {Missing}", panel.Summary);

        failTime = false;
        await panel.RetryAsync(Draft(), []);

        Assert.Equal([Destination.TimeEntry], calls[1]);
        Assert.True(panel.Published);
        Assert.Empty(panel.Failed);
        Assert.Equal($"Published to ticket #48213 · 0:30 logged. {Missing}", panel.Summary);
    }

    [Fact]
    public async Task AKnowledgeBaseThatNeedsAMappingSaysWhichCompanyAndOffersTheChoices()
    {
        // ST-097 AC2: unmatched → prompt once at publish. The backend has said the ticket's company is
        // not mapped; the pane asks for the platform's companies and shows the prompt beside the result.
        var panel = Panel(
            publish: r => [.. r.Destinations.Select(d => d == Destination.KbArticle
                ? new DestinationResult(d, false, null, "\"Acme Dental\" is not mapped to a company in the documentation platform. Map it in Settings → Integrations, then publish the article again.", "needs_mapping")
                : new DestinationResult(d, true))],
            companies: () => [new CompanyChoice("7", "Acme Dental"), new CompanyChoice("9", "Bright Smiles")]);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.KbArticle = true;

        await panel.PublishAsync(Draft(), []);

        Assert.True(panel.NeedsMapping);
        Assert.Equal("Acme Dental", panel.CompanyToMap);
        Assert.Equal(["Acme Dental", "Bright Smiles"], (await panel.CompanyChoicesAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task MappingTheCompanyRemembersItAndRetriesOnlyTheArticle()
    {
        var mapped = new List<(string Psa, string Doc)>();
        var mappedYet = false;
        var calls = new List<IReadOnlySet<Destination>>();
        var panel = Panel(
            publish: r =>
            {
                calls.Add(r.Destinations);
                return [.. r.Destinations.Select(d => d == Destination.KbArticle && !mappedYet
                    ? new DestinationResult(d, false, null, "not mapped", "needs_mapping")
                    : new DestinationResult(d, true, new Uri("https://x.example/1")))];
            },
            companies: () => [new CompanyChoice("7", "Acme Dental")],
            map: (psa, doc) => { mapped.Add((psa, doc)); mappedYet = true; return true; });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.KbArticle = true;
        await panel.PublishAsync(Draft(), []);

        var ok = await panel.MapAndRetryAsync("7", Draft(), []);

        Assert.True(ok);
        Assert.Equal([("Acme Dental", "7")], mapped);
        Assert.False(panel.NeedsMapping);
        Assert.True(panel.Published);
        Assert.Equal([Destination.KbArticle], calls[1]);
    }

    [Fact]
    public async Task AMappingTheBackendRefusedLeavesThePromptUp()
    {
        var panel = Panel(
            publish: r => [.. r.Destinations.Select(d => new DestinationResult(d, false, null, "not mapped", "needs_mapping"))],
            companies: () => [new CompanyChoice("7", "Acme Dental")],
            map: (_, _) => false);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.KbArticle = true;
        await panel.PublishAsync(Draft(), []);

        Assert.False(await panel.MapAndRetryAsync("7", Draft(), []));
        Assert.True(panel.NeedsMapping);
    }

    [Fact]
    public async Task PublishingWhileBlockedIsRefusedNotAttempted()
    {
        var attempted = false;
        var panel = Panel(publish: _ => { attempted = true; return []; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));

        Assert.False(attempted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingAgainAfterSomethingLandedIsRefused(bool partly)
    {
        // T-129: Ctrl+Enter reaches PublishAsync whatever the pane shows. Once a note or a time entry has
        // landed, a second Publish posts it again (a duplicate the customer can see, and time billed
        // twice). Retry, which sends only what failed, is the one way on.
        var calls = 0;
        var panel = Panel(publish: request =>
        {
            calls++;
            return [.. request.Destinations.Select(d => new DestinationResult(d, !(partly && d == Destination.TimeEntry), null, partly && d == Destination.TimeEntry ? "ConnectWise refused the member" : null))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        Assert.False(panel.CanPublish);
        Assert.Equal(partly ? PublishPanel.PartlyPublished : PublishPanel.AlreadyPublished, panel.BlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChangingTheDestinationsAfterALandingDoesNotReopenPublish()
    {
        // Ticking the KB article after the note landed would send the note again with it.
        var panel = Panel();
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.KbArticle = false;
        await panel.PublishAsync(Draft(), []);

        panel.KbArticle = true;
        panel.TicketNote = false;
        panel.TimeEntry = false;

        Assert.False(panel.CanPublish);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));
    }

    [Fact]
    public async Task WhenNothingLandedPublishIsStillOffered()
    {
        // Nothing reached the ticket, so asking again duplicates nothing.
        var calls = 0;
        var panel = Panel(publish: r => { calls++; return [.. r.Destinations.Select(d => new DestinationResult(d, false, null, "The backend did not answer."))]; });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        Assert.True(panel.CanPublish);
        await panel.PublishAsync(Draft(), []);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RetryAfterAPartialPublishStillSendsWhatFailed()
    {
        var calls = new List<IReadOnlySet<Destination>>();
        var panel = Panel(publish: r =>
        {
            calls.Add(r.Destinations);
            return [.. r.Destinations.Select(d => new DestinationResult(d, d != Destination.TimeEntry || calls.Count > 1))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        await panel.RetryAsync(Draft(), []);

        Assert.Equal([Destination.TimeEntry], calls[1]);
        Assert.True(panel.Published);
        Assert.False(panel.CanPublish);
    }

    [Theory]
    [InlineData(DestinationResult.Unknown)]
    [InlineData(DestinationResult.TimedOut)]
    public async Task AnAnswerThatMayHaveLandedBlocksPublishingAgainUntilTheTicketIsChecked(string kind)
    {
        // The pipe dropped after publish_session went out (a lost answer is "unknown"), or the backend ran
        // out of time with the call on its way: the note may be on the ticket. Nothing came back Ok, so
        // the T-129 guard alone let Ctrl+Enter post it a second time.
        var sent = new List<PublishRequest>();
        var panel = Panel(publish: r =>
        {
            sent.Add(r);
            return sent.Count == 1
                ? [.. r.Destinations.Select(d => new DestinationResult(d, false, null, "It may already be on the ticket.", kind))]
                : [.. r.Destinations.Select(d => new DestinationResult(d, true))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        Assert.True(panel.MayHaveLanded);
        Assert.All(panel.Results, r => Assert.True(r.MayHaveLanded));
        Assert.Equal("Note not confirmed: It may already be on the ticket.", panel.Summary);
        Assert.False(panel.CanPublish);
        Assert.False(panel.CanRetry);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.RetryBlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.RetryAsync(Draft(), []));
        Assert.Single(sent);
        Assert.False(sent[0].PublishAgain);

        // The technician says they looked; the next send says so to the service, and the confirmation
        // is spent on it.
        panel.TicketChecked = true;

        Assert.True(panel.CanPublish);
        Assert.True(panel.CanRetry);
        await panel.RetryAsync(Draft(), []);
        Assert.Equal(2, sent.Count);
        Assert.True(sent[1].PublishAgain);
        Assert.False(panel.TicketChecked);
        Assert.True(panel.Published);
    }

    [Fact]
    public async Task AConfirmationIsSpentOnTheOneSendItWasGivenFor()
    {
        // A second uncertain answer needs a second look: the first "I checked" was about the first one.
        var sent = new List<PublishRequest>();
        var panel = Panel(publish: r =>
        {
            sent.Add(r);
            return [.. r.Destinations.Select(d => new DestinationResult(d, false, null, "It may already be on the ticket.", DestinationResult.Unknown))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);
        panel.TicketChecked = true;
        await panel.PublishAsync(Draft(), []);

        Assert.False(panel.TicketChecked);
        Assert.False(panel.CanPublish);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task ATickGivenWhileASendIsOnItsWayDoesNotOutliveIt()
    {
        // Review of #265 (m1). The technician ticks, publishes, and ticks again while that send is still
        // out. Its answer is lost too: the second tick was about the first answer, not this one, so the
        // next send waits for a fresh tick and cannot carry publish_again without it.
        var sent = new List<PublishRequest>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            async (r, _) =>
            {
                sent.Add(r);
                if (sent.Count == 2)
                {
                    await gate.Task;
                }

                return sent.Count <= 2
                    ? [.. r.Destinations.Select(d => new DestinationResult(d, false, null, "It may already be on the ticket.", DestinationResult.Unknown))]
                    : [.. r.Destinations.Select(d => new DestinationResult(d, true))];
            });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);
        panel.TicketChecked = true;

        var second = panel.PublishAsync(Draft(), []);
        panel.TicketChecked = true;
        gate.SetResult();
        await second;

        Assert.True(sent[1].PublishAgain);
        Assert.False(panel.TicketChecked);
        Assert.False(panel.CanPublish);
        Assert.False(panel.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.RetryAsync(Draft(), []));
        Assert.Equal(2, sent.Count);

        panel.TicketChecked = true;
        await panel.RetryAsync(Draft(), []);
        Assert.True(sent[2].PublishAgain);
    }

    [Fact]
    public async Task ASessionTheServiceSaysIsAlreadyPublishedIsSaidAsThatAndHeldTheSameWay()
    {
        // Review of #265 (m5). Part of it is on the ticket, so "not confirmed" would undersell it.
        var panel = Panel(publish: r => [.. r.Destinations.Select(d => new DestinationResult(d, false, null, PublishCommands.AlreadyPublished, DestinationResult.AlreadyPublished))]);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        Assert.Equal("Already published: check the ticket before publishing again", panel.Summary);
        Assert.True(panel.MayHaveLanded);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
    }

    [Fact]
    public async Task APartialPublishWithADestinationThatMayHaveLandedHoldsRetryUntilChecked()
    {
        // The note landed and the time entry's answer was lost. Retry sends only the time entry, and that
        // is the one that may already be on the ticket.
        var sent = new List<PublishRequest>();
        var panel = Panel(publish: r =>
        {
            sent.Add(r);
            return [.. r.Destinations.Select(d => d == Destination.TimeEntry && sent.Count == 1
                ? new DestinationResult(d, false, null, "It may already be on the ticket.", DestinationResult.Unknown)
                : new DestinationResult(d, true))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        Assert.True(panel.PartiallyPublished);
        Assert.False(panel.CanRetry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.RetryAsync(Draft(), []));

        panel.TicketChecked = true;
        await panel.RetryAsync(Draft(), []);

        Assert.Equal([Destination.TimeEntry], sent[1].Destinations);
        Assert.True(panel.Published);

        // Checked or not, Publish stays refused once something landed (T-129).
        panel.TicketChecked = true;
        Assert.Equal(PublishPanel.AlreadyPublished, panel.BlockedBecause);
    }

    [Fact]
    public async Task ARetryAfterAPartialPublishGoesWithoutTheFlag()
    {
        // The service checks each destination sent, and one that plainly failed is not held, so Retry of
        // it beside a landing needs nobody's word. The flag is the technician's tick alone: a pane that saw
        // the note land must not vouch for a time entry another window has since left unknown (re-review
        // of #265 at bde6a99, which withdrew the earlier silent flag on Retry).
        var sent = new List<PublishRequest>();
        var panel = Panel(publish: r =>
        {
            sent.Add(r);
            return [.. r.Destinations.Select(d => new DestinationResult(d, d != Destination.TimeEntry || sent.Count > 1))];
        });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);
        await panel.RetryAsync(Draft(), []);

        Assert.Equal([Destination.TimeEntry], sent[1].Destinations);
        Assert.All(sent, r => Assert.False(r.PublishAgain));
    }

    [Fact]
    public async Task APlainFailureNeedsNoConfirmation()
    {
        // A refusal that never reached the PSA duplicates nothing: no box to tick, no flag on the wire.
        var sent = new List<PublishRequest>();
        var panel = Panel(publish: r => { sent.Add(r); return [.. r.Destinations.Select(d => new DestinationResult(d, false, null, "Connect a PSA to publish."))]; });
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        Assert.False(panel.MayHaveLanded);
        Assert.True(panel.CanPublish);
        await panel.PublishAsync(Draft(), []);
        Assert.All(sent, r => Assert.False(r.PublishAgain));
    }

    // O3 (owner, review of #265): a reopened Review, or the pane after the service refused a session
    // already published or unconfirmed, offers each destination as the last publish left it. Landed and
    // unknown ones start unticked, and sending either needs the check; a plainly failed one is ticked and
    // goes without it (ST-094 AC3).

    [Fact]
    public async Task AReopenedPanelAfterAllUnknownRowsHoldsThemBehindTheCheck()
    {
        var sent = new List<PublishRequest>();
        var panel = Reopened(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Unknown, [Destination.TimeEntry] = EarlierOutcome.Unknown }, sent);

        Assert.False(panel.TicketNote);
        Assert.False(panel.TimeEntry);
        Assert.Equal(EarlierOutcome.Unknown, panel.EarlierFor(Destination.TicketNote));
        Assert.Equal("Choose at least one destination", panel.BlockedBecause);

        panel.TicketNote = true;

        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));

        panel.TicketChecked = true;
        await panel.PublishAsync(Draft(), []);

        var request = Assert.Single(sent);
        Assert.Equal([Destination.TicketNote], request.Destinations);
        Assert.True(request.PublishAgain);
        Assert.Equal(EarlierOutcome.Landed, panel.EarlierFor(Destination.TicketNote));
    }

    [Fact]
    public async Task SendingOnlyWhatPlainlyFailedNeedsNoCheck()
    {
        // The note landed last time and the time entry plainly failed: the time entry is ticked, the
        // note is not, and Publish sends the time entry with no box to tick. Not with publish_again:
        // nobody looked, and the service checks each destination sent against what it holds (MJ1).
        var sent = new List<PublishRequest>();
        var panel = Reopened(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed }, sent);

        Assert.False(panel.TicketNote);
        Assert.True(panel.TimeEntry);
        Assert.Null(panel.BlockedBecause);

        await panel.PublishAsync(Draft(), []);

        Assert.Equal([Destination.TimeEntry], Assert.Single(sent).Destinations);
        Assert.False(sent[0].PublishAgain);
    }

    [Fact]
    public async Task ALandedDestinationTickedAgainNeedsTheCheck()
    {
        var panel = Reopened(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed }, []);

        panel.TicketNote = true;

        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));
    }

    [Theory]
    [InlineData(DestinationResult.AlreadyPublished)]
    [InlineData(DestinationResult.SessionUnconfirmed)]
    public async Task ARefusalForAnEarlierPublishReloadsItsOutcomesAndOffersTheChoice(string kind)
    {
        // The pane did not know (another window published, or the service remembered an unconfirmed
        // attempt): the service refuses, the pane asks what is on the ticket and opens the choice.
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, false, null, "refused", kind))]),
            reloadEarlier: _ => Task.FromResult<IReadOnlyDictionary<Destination, EarlierOutcome>>(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed, [Destination.TimeEntry] = EarlierOutcome.Unknown }));
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        await panel.PublishAsync(Draft(), []);

        Assert.Empty(panel.Results);
        Assert.False(panel.TicketNote);
        Assert.False(panel.TimeEntry);
        Assert.Equal(EarlierOutcome.Landed, panel.EarlierFor(Destination.TicketNote));
        Assert.Equal(EarlierOutcome.Unknown, panel.EarlierFor(Destination.TimeEntry));
        Assert.Null(panel.EarlierFor(Destination.KbArticle));
    }

    [Fact]
    public async Task AfterTheReloadTheRefusalIsSaid()
    {
        // m-g: the result list is cleared for the choice, so the reason has to be said somewhere the
        // screen reader hears it.
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, false, null, PublishCommands.AlreadyPublished, DestinationResult.AlreadyPublished))]),
            reloadEarlier: _ => Task.FromResult<IReadOnlyDictionary<Destination, EarlierOutcome>>(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed }));
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        Assert.Null(panel.EarlierRefusal);

        await panel.PublishAsync(Draft(), []);

        Assert.Equal(PublishCommands.AlreadyPublished, panel.EarlierRefusal);
    }

    [Fact]
    public async Task AReloadThatFindsNothingKeepsTheRefusalAndItsCheck()
    {
        // m-a: the service said "may already be there" and the reload came back empty (a dropped pipe, a
        // session gone). The rows stay, so the box to tick shows and Publish never loops silently.
        var sent = new List<PublishRequest>();
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) =>
            {
                sent.Add(r);
                return Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, false, null, PublishCommands.Unconfirmed, DestinationResult.SessionUnconfirmed))]);
            },
            reloadEarlier: _ => Task.FromResult<IReadOnlyDictionary<Destination, EarlierOutcome>>(new Dictionary<Destination, EarlierOutcome>()));
        panel.KbArticle = false;
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        await panel.PublishAsync(Draft(), []);

        Assert.Equal(2, panel.Results.Count);
        Assert.True(panel.MayHaveLanded);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PublishAsync(Draft(), []));

        panel.TicketChecked = true;
        await panel.RetryAsync(Draft(), []);
        Assert.True(sent[1].PublishAgain);
    }

    [Fact]
    public async Task AWholeRequestRefusalDoesNotForgetAnEarlierUnknown()
    {
        // m-c: "the backend did not answer" for the whole request says nothing about the ticket. The note
        // that may have landed before still may have, and stays behind the check.
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, false, null, "The backend did not answer.", DestinationResult.Refused))]),
            earlierOutcomes: new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Unknown });
        panel.KbArticle = false;
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.TicketNote = true;
        panel.TicketChecked = true;

        await panel.PublishAsync(Draft(), []);

        Assert.Equal(EarlierOutcome.Unknown, panel.EarlierFor(Destination.TicketNote));
        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
    }

    [Fact]
    public async Task AnEarlierOutcomeStillNeedsTheCheckOnceThereAreResults()
    {
        // m-d: the time entry plainly failed in this pane, so there are results and nothing landed.
        // Ticking the note, which landed last time, still needs the check.
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, false, null, "ConnectWise refused the member", "forbidden"))]),
            earlierOutcomes: new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed });
        panel.KbArticle = false;
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);
        Assert.NotEmpty(panel.Results);

        panel.TicketNote = true;

        Assert.Equal(PublishPanel.CheckTheTicketFirst, panel.BlockedBecause);
    }

    private static PublishPanel Reopened(IReadOnlyDictionary<Destination, EarlierOutcome> earlier, List<PublishRequest> sent)
    {
        var panel = new PublishPanel(
            Session(draft: Draft()),
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) =>
            {
                sent.Add(r);
                return Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, true))]);
            },
            earlierOutcomes: earlier);
        panel.KbArticle = false;
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        return panel;
    }

    private static PublishPanel Panel(
        Session? session = null,
        IReadOnlyList<string>? integrations = null,
        Func<string, IReadOnlyList<TicketMatch>>? search = null,
        Func<PublishRequest, IReadOnlyList<DestinationResult>>? publish = null,
        TimeEntryOptions? rounding = null,
        Func<IReadOnlyList<CompanyChoice>>? companies = null,
        Func<string, string, bool>? map = null) => new(
            session ?? Session(draft: Draft()),
            integrations ?? ConnectWise,
            (q, _) => Task.FromResult(search?.Invoke(q) ?? []),
            (r, _) => Task.FromResult(publish?.Invoke(r) ?? [.. r.Destinations.Select(d => new DestinationResult(d, true))]),
            rounding,
            mapping: companies is null ? null : new CompanyMapping(
                _ => Task.FromResult(companies()),
                (psa, doc, _) => Task.FromResult(map?.Invoke(psa, doc) ?? true)));
}
