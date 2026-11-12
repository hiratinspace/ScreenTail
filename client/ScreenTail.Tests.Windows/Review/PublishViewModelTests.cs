using System.Runtime.Versioning;
using ScreenTail.Core.Review.Publish;
using ScreenTail.UI.Review.Publish;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The publish pane's binding layer over <see cref="PublishPanel"/> (ST-078; 2026-10-12 review T-171,
/// T-386): the two radio buttons land on one note type, and the Minutes field cannot publish a number
/// it does not show.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PublishViewModelTests
{
    private static readonly string[] ConnectWise = ["connectwise"];

    [Fact]
    public async Task TheNoteGoesAsTheRadioLastChosen()
    {
        // T-171. A WPF radio group turns the old button off and the new one on in whichever order the
        // bindings fire; both orders, and going back, must publish the one that is lit.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        Assert.Equal(NoteType.Internal, await PublishedAsAsync(_ => { }));
        Assert.Equal(NoteType.Discussion, await PublishedAsAsync(pane => { pane.IsInternal = false; pane.IsDiscussion = true; }));
        Assert.Equal(NoteType.Discussion, await PublishedAsAsync(pane => { pane.IsDiscussion = true; pane.IsInternal = false; }));
        Assert.Equal(NoteType.Internal, await PublishedAsAsync(pane =>
        {
            pane.IsDiscussion = true;
            pane.IsInternal = false;
            pane.IsInternal = true;
            pane.IsDiscussion = false;
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public async Task ATimeEntryIsNeverSentWithANumberTheFieldDoesNotShow(string typed)
    {
        // T-386. int.TryParse failing used to leave the panel on the previous number, so a blank or a
        // decimal field published a billable value the technician never saw.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var sent = new List<PublishRequest>();
        var pane = Pane(sent);
        Assert.Equal("30", pane.Minutes);
        Assert.True(pane.CanPublish);

        pane.Minutes = typed;

        Assert.False(pane.CanPublish);
        Assert.Contains("whole", pane.BlockedBecause, StringComparison.OrdinalIgnoreCase);
        await pane.PublishCommand.ExecuteAsync(null);
        Assert.Empty(sent);

        // The field keeps showing what was typed while another control refreshes the pane.
        pane.KbArticle = true;
        Assert.Equal(typed, pane.Minutes);
        Assert.False(pane.CanPublish);

        pane.Minutes = "45";

        Assert.True(pane.CanPublish);
        Assert.Equal(string.Empty, pane.BlockedBecause);
        await pane.PublishCommand.ExecuteAsync(null);
        Assert.Equal(45, Assert.Single(sent).Minutes);
    }

    [Fact]
    public async Task ARetryIsHeldByTheMinutesOnlyWhenTheTimeEntryIsWhatItSends()
    {
        // Retry sends only what failed. A time entry that failed goes again with the field's number, so a
        // field that is not a whole number holds it; a ticket note that failed carries no minutes, so the
        // same field does not.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var sent = new List<PublishRequest>();
        var pane = Pane(sent, landed: d => d != Destination.TimeEntry);
        await pane.PublishCommand.ExecuteAsync(null);
        pane.Minutes = "1.5";

        await pane.RetryCommand.ExecuteAsync(null);
        Assert.Single(sent);

        pane.Minutes = "45";
        await pane.RetryCommand.ExecuteAsync(null);
        var retried = sent[^1];
        Assert.Equal([Destination.TimeEntry], retried.Destinations);
        Assert.Equal(45, retried.Minutes);

        var notes = new List<PublishRequest>();
        var other = Pane(notes, landed: d => d != Destination.TicketNote);
        await other.PublishCommand.ExecuteAsync(null);
        other.Minutes = "1.5";

        await other.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, notes.Count);
        Assert.Equal([Destination.TicketNote], notes[^1].Destinations);
    }

    [Fact]
    public async Task AnAnswerThatMayHaveLandedHoldsCtrlEnterAndRetryUntilTheBoxIsTicked()
    {
        // The pipe dropped after publish_session went out. The warning and its checkbox show, the row is
        // marked apart from a failure, and neither the shortcut nor Retry sends until the box is ticked.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var sent = new List<PublishRequest>();
        var session = Session(Frames(1));
        var panel = new PublishPanel(
            session,
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (request, _) =>
            {
                sent.Add(request);
                return Task.FromResult<IReadOnlyList<DestinationResult>>(sent.Count == 1
                    ? [.. request.Destinations.Select(d => new DestinationResult(d, false, null, "It may already be on the ticket.", DestinationResult.Unknown))]
                    : [.. request.Destinations.Select(d => new DestinationResult(d, true))]);
            });
        var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
        pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");
        await pane.PublishCommand.ExecuteAsync(null);

        Assert.Equal(System.Windows.Visibility.Visible, pane.CheckTicketVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, pane.RetryVisibility);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, pane.CheckTicketPrompt);
        Assert.Equal("I checked ticket #48213 and want to publish again", pane.CheckTicketLabel);
        Assert.All(pane.Results, row => Assert.Equal(("?", "Not confirmed"), (row.Mark, row.MarkName)));
        Assert.False(pane.CanPublish);
        Assert.False(pane.CanRetry);
        Assert.True(pane.CanCheckTicket);
        await pane.PublishCommand.ExecuteAsync(null);
        await pane.RetryCommand.ExecuteAsync(null);
        Assert.Single(sent);

        pane.TicketChecked = true;

        Assert.True(pane.CanRetry);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, pane.CheckTicketPrompt);
        await pane.RetryCommand.ExecuteAsync(null);
        Assert.Equal(2, sent.Count);
        Assert.True(sent[1].PublishAgain);
        Assert.False(pane.TicketChecked);
        Assert.Equal(System.Windows.Visibility.Collapsed, pane.CheckTicketVisibility);
    }

    [Fact]
    public async Task AReopenedPaneMarksEachDestinationAndHoldsTheOnesThatMayDuplicate()
    {
        // O3: the note may have landed and the time entry plainly failed. The note starts unticked and
        // marked, read as "not confirmed"; the time entry goes alone without the box; the note needs it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var sent = new List<PublishRequest>();
        var session = Session(Frames(1));
        var panel = new PublishPanel(
            session,
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (request, _) =>
            {
                sent.Add(request);
                return Task.FromResult<IReadOnlyList<DestinationResult>>([.. request.Destinations.Select(d => new DestinationResult(d, true))]);
            },
            earlierOutcomes: new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Unknown });
        var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
        pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");

        Assert.False(pane.TicketNote);
        Assert.Equal("? Ticket note  (not confirmed)", pane.TicketNoteLabel);
        Assert.Equal("Ticket note, not confirmed", pane.TicketNoteName);
        Assert.Equal(System.Windows.Visibility.Visible, pane.EarlierVisibility);
        Assert.True(pane.CanPublish);

        pane.TicketNote = true;
        Assert.False(pane.CanPublish);
        Assert.Equal(PublishPanel.CheckTheTicketFirst, pane.BlockedBecause);

        pane.TicketChecked = true;
        Assert.True(pane.CanPublish);
        await pane.PublishCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(sent).PublishAgain);
    }

    [Fact]
    public async Task ARefusalForAnEarlierPublishReopensTheChoiceAndSaysWhy()
    {
        // O3 and m-g: the service refused a publish another window made. The pane reloads what is on the
        // ticket, opens the form on it, and the live region says the refusal, since the result list that
        // would have said it is gone. m-f: an article is in the knowledge base, not on the ticket.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var session = Session(Frames(1));
        var panel = new PublishPanel(
            session,
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (request, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. request.Destinations.Select(d => new DestinationResult(d, false, null, PublishCommands.AlreadyPublished, DestinationResult.AlreadyPublished))]),
            reloadEarlier: _ => Task.FromResult<IReadOnlyDictionary<Destination, EarlierOutcome>>(new Dictionary<Destination, EarlierOutcome> { [Destination.TicketNote] = EarlierOutcome.Landed, [Destination.KbArticle] = EarlierOutcome.Landed }));
        var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
        pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");

        await pane.PublishCommand.ExecuteAsync(null);

        Assert.Equal(System.Windows.Visibility.Visible, pane.FormVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, pane.EarlierVisibility);
        Assert.StartsWith(PublishCommands.AlreadyPublished, pane.EarlierPrompt, StringComparison.Ordinal);
        Assert.False(pane.TicketNote);
        Assert.Equal("Ticket note  (already on the ticket)", pane.TicketNoteLabel);
        Assert.EndsWith("(already in the knowledge base)", pane.KbLabel, StringComparison.Ordinal);
        Assert.EndsWith(", already in the knowledge base", pane.KbName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiscussionHintIsBoundAndSaysNoProductDuringTheDemo()
    {
        // Review 2026-10-27: the XAML line was fixed text naming ConnectWise, shown during the demo.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var real = Pane([]);
        var demo = Pane([], integrations: ["demo-psa", "demo-kb"]);

        Assert.Equal("Discussion notes are visible to the customer in ConnectWise.", real.DiscussionHint);
        Assert.Equal(System.Windows.Visibility.Collapsed, real.DemoVisibility);
        Assert.Equal("Discussion notes would be visible to the customer in a real PSA.", demo.DiscussionHint);
        Assert.Equal(System.Windows.Visibility.Visible, demo.DemoVisibility);
        Assert.StartsWith("Publishing to Demo PSA (not ConnectWise)", demo.DemoNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileARetryIsOnItsWayEverySendIsOffAndASecondRetrySendsNothing()
    {
        // Review of #274: the command's Execute does not look at whether it is running. A second Retry
        // under the first reached the panel's "already in flight" throw, which nothing caught.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var sent = new List<PublishRequest>();
            var held = new TaskCompletionSource();
            var session = Session(Frames(1));
            var panel = new PublishPanel(
                session,
                ConnectWise,
                (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
                async (request, _) =>
                {
                    sent.Add(request);
                    if (sent.Count > 1)
                    {
                        await held.Task.ConfigureAwait(true);
                    }

                    return [.. request.Destinations.Select(d => new DestinationResult(d, sent.Count > 1 || d != Destination.TimeEntry))];
                });
            var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
            pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");
            await pane.PublishCommand.ExecuteAsync(null);
            Assert.True(pane.CanRetry);

            var first = pane.RetryCommand.ExecuteAsync(null);

            Assert.False(pane.RetryCommand.CanExecute(null));
            Assert.False(pane.PublishCommand.CanExecute(null));
            Assert.False(pane.MapAndPublishCommand.CanExecute(null));
            pane.RetryCommand.Execute(null);
            Assert.False(pane.RetryCommand.CanExecute(null));
            Assert.Equal(2, sent.Count);

            held.SetResult();
            await first;

            Assert.Equal(2, sent.Count);
            Assert.True(pane.RetryCommand.CanExecute(null));
        });
    }

    [Fact]
    public void WhileAMappingIsOnItsWayASecondPressMapsAndSendsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var sent = new List<PublishRequest>();
            var maps = 0;
            var held = new TaskCompletionSource<bool>();
            var session = Session(Frames(1));
            var panel = new PublishPanel(
                session,
                ConnectWise,
                (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
                (request, _) =>
                {
                    sent.Add(request);
                    var mapped = maps > 0;
                    return Task.FromResult<IReadOnlyList<DestinationResult>>([.. request.Destinations.Select(d => d == Destination.KbArticle && !mapped
                        ? new DestinationResult(d, false, null, "not mapped", "needs_mapping")
                        : new DestinationResult(d, true))]);
                },
                mapping: new CompanyMapping(
                    _ => Task.FromResult<IReadOnlyList<CompanyChoice>>([new CompanyChoice("7", "Acme Dental")]),
                    (_, _, _) =>
                    {
                        maps++;
                        return held.Task;
                    }));
            var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
            pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");
            pane.KbArticle = true;
            await pane.PublishCommand.ExecuteAsync(null);
            Assert.True(panel.NeedsMapping);
            pane.SelectedCompany = new CompanyChoice("7", "Acme Dental");

            var first = pane.MapAndPublishCommand.ExecuteAsync(null);

            Assert.False(pane.MapAndPublishCommand.CanExecute(null));
            Assert.False(pane.RetryCommand.CanExecute(null));
            pane.MapAndPublishCommand.Execute(null);
            Assert.Equal(1, maps);

            held.SetResult(true);
            await first;

            Assert.Equal(1, maps);
            Assert.Equal(2, sent.Count);
            Assert.Equal([Destination.KbArticle], sent[1].Destinations);
        });
    }

    private static async Task<NoteType> PublishedAsAsync(Action<PublishViewModel> choose)
    {
        var sent = new List<PublishRequest>();
        var pane = Pane(sent);
        choose(pane);
        await pane.PublishCommand.ExecuteAsync(null);
        return Assert.Single(sent).NoteType;
    }

    private static PublishViewModel Pane(List<PublishRequest> sent, Func<Destination, bool>? landed = null, IReadOnlyList<string>? integrations = null)
    {
        var session = Session(Frames(1));
        var tries = 0;
        var panel = new PublishPanel(
            session,
            integrations ?? ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (request, _) =>
            {
                sent.Add(request);

                // The first try goes as `landed` says; a retry lands.
                var first = tries++ == 0;
                return Task.FromResult<IReadOnlyList<DestinationResult>>([.. request.Destinations.Select(d => new DestinationResult(d, !first || landed?.Invoke(d) != false))]);
            });
        var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
        pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");
        return pane;
    }
}
