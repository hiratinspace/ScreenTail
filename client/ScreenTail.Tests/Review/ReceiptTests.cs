using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The receipt after publishing (Spec v0.6.0 §5 S3): a state of the session pane, never a dialog. What
/// landed where, one row per destination with its state in a word and a provider-named link, the
/// session's facts, and the line that says editing happens in the PSA from here on.
/// </summary>
public sealed class ReceiptTests
{
    private static readonly string[] Both = ["connectwise", "hudu"];
    private static readonly Uri Ticket = new("https://na.myconnectwise.net/ticket/48213");
    private static readonly Uri Article = new("https://docs.contosomsp.com/kba/printer");

    [Fact]
    public async Task AllSentIsOneRowPerDestinationEachSentWithItsProvidersLink()
    {
        var panel = await PublishedAsync(d => new DestinationResult(d, true, d == Destination.KbArticle ? Article : Ticket));

        Assert.Equal(ReceiptState.Sent, panel.ReceiptState);
        Assert.Equal("Published to ticket #48213", panel.ReceiptHeading);
        Assert.Equal([Destination.TicketNote, Destination.TimeEntry, Destination.KbArticle], panel.Rows.Select(r => r.Destination));
        Assert.All(panel.Rows, r => Assert.Equal("Sent", r.Badge));
        Assert.Equal(["Open in ConnectWise", "Open in ConnectWise", "Open in Hudu"], panel.Rows.Select(r => r.LinkText));
    }

    [Fact]
    public async Task APartialPublishSaysSoAndOffersRetryForWhatFailed()
    {
        var panel = await PublishedAsync(d => d == Destination.TimeEntry
            ? new DestinationResult(d, false, null, "ConnectWise refused the member")
            : new DestinationResult(d, true, d == Destination.KbArticle ? Article : Ticket));

        Assert.Equal(ReceiptState.Partial, panel.ReceiptState);
        Assert.Equal("Partly published to ticket #48213", panel.ReceiptHeading);
        Assert.Equal(["Sent", "Failed", "Sent"], panel.Rows.Select(r => r.Badge));
        Assert.True(panel.CanRetry);
        Assert.Equal("Retry ConnectWise", panel.RetryLabel);
    }

    [Fact]
    public async Task OneThatMayHaveLandedIsNotConfirmedNeverFailed()
    {
        var panel = await PublishedAsync(d => new DestinationResult(d, false, null, "no answer", DestinationResult.Unknown), only: Destination.TicketNote);

        Assert.Equal(ReceiptState.NotConfirmed, panel.ReceiptState);
        Assert.Equal("Not confirmed on ticket #48213", panel.ReceiptHeading);
        Assert.Equal("Not confirmed", Assert.Single(panel.Rows).Badge);
        Assert.DoesNotContain("failed", panel.ReceiptHeading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NothingLandedIsNotPublished()
    {
        var panel = await PublishedAsync(d => new DestinationResult(d, false, null, "ConnectWise is down"), only: Destination.TicketNote);

        Assert.Equal(ReceiptState.Failed, panel.ReceiptState);
        Assert.Equal("Not published to ticket #48213", panel.ReceiptHeading);
    }

    [Fact]
    public async Task BeforeAnyPublishThereIsNoReceipt()
    {
        var panel = Panel(_ => new DestinationResult(Destination.TicketNote, true));

        Assert.Null(panel.ReceiptState);
        Assert.Null(panel.ReceiptHeading);
        Assert.Empty(panel.ReceiptFacts);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EachRowSaysWhatWentThere()
    {
        var panel = await PublishedAsync(d => new DestinationResult(d, true, Ticket), draft: Draft() with { SuggestedTitle = "Restart a stopped Print Spooler" });

        Assert.Equal("Internal note", panel.DetailOf(Destination.TicketNote));
        Assert.Equal("0:30", panel.DetailOf(Destination.TimeEntry));
        Assert.Equal("“Restart a stopped Print Spooler”", panel.DetailOf(Destination.KbArticle));
    }

    [Fact]
    public async Task TheFactsAreTheSessionsCaptureStepsScreenshotsMaskingAndTime()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 11, 14, 20, 0, TimeSpan.Zero));
        var frames = new[] { Frame("f1", masked: 2), Frame("f2", masked: 0), Frame("f3", masked: 1) };
        var session = Session(draft: Draft()) with { DurationMs = 12 * 60_000, Frames = frames };
        var panel = new PublishPanel(
            session,
            Both,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(38));
                return Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, true, Ticket))]);
            },
            time: time);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));

        // Two of the three steps were checked: one inferred and confirmed, one inferred and not.
        var note = Draft(Step("a"), Step("b", StepConfidence.Low) with { Confirmed = true }, Step("c", StepConfidence.Low));
        await panel.PublishAsync(note, ["f1", "f3"]);

        Assert.Equal(
            [("12 min", "captured"), ("2 of 3", "steps checked"), ("2", "screenshots"), ("3", "items masked"), ("38 s", "open to published")],
            panel.ReceiptFacts.Select(f => (f.Value, f.Caption)));
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 14, 20, 38, TimeSpan.Zero), panel.SentAt);
    }

    [Fact]
    public async Task TheChangeLineNamesThePsaOnceTheNoteLanded()
    {
        var panel = await PublishedAsync(d => new DestinationResult(d, true, Ticket));

        Assert.Equal("Need to change something? Edit the note in ConnectWise. Publishing again from here would add a second note.", panel.ChangeNote);
    }

    [Fact]
    public async Task NoChangeLineWhenTheNoteDidNotLand()
    {
        var panel = await PublishedAsync(d => new DestinationResult(d, false, null, "down"), only: Destination.TicketNote);

        Assert.Null(panel.ChangeNote);
    }

    [Fact]
    public async Task TheSublineIsCompanyTitleAndWhen()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 11, 14, 31, 0, TimeSpan.Zero));
        var panel = new PublishPanel(
            Session(draft: Draft()),
            Both,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, true, Ticket))]),
            time: time);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        await panel.PublishAsync(Draft(), []);

        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        Assert.Equal($"Acme Dental · Printer offline · {local:HH:mm}", panel.ReceiptSubline);
    }

    [Fact]
    public async Task TheReceiptCopyHasNoDashes()
    {
        var panel = await PublishedAsync(d => d == Destination.TimeEntry ? new DestinationResult(d, false, null, "refused") : new DestinationResult(d, true, Ticket));
        string?[] copy = [panel.ReceiptHeading, panel.ChangeNote, panel.ReceiptSubline, .. panel.ReceiptFacts.Select(f => f.Caption)];

        Assert.All(copy, line => Assert.DoesNotContain("—", line ?? string.Empty, StringComparison.Ordinal));
    }

    private static Frame Frame(string id, int masked) => new()
    {
        Id = id,
        TsMs = 0,
        Trigger = FrameTrigger.Click,
        Image = $"{id}.png",
        Width = 1920,
        Height = 1080,
        RedactionPending = false,
        MaskedRegions = [.. Enumerable.Range(0, masked).Select(_ => new MaskedRegion { X = 0, Y = 0, Width = 10, Height = 10, Kind = MaskKind.Password })],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    private static PublishPanel Panel(Func<Destination, DestinationResult> result, DraftNote? draft = null) => new(
        Session(draft: draft ?? Draft()),
        Both,
        (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
        (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(result)]));

    private static async Task<PublishPanel> PublishedAsync(Func<Destination, DestinationResult> result, Destination? only = null, DraftNote? draft = null)
    {
        var panel = Panel(result, draft);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.TicketNote = only is null or Destination.TicketNote;
        panel.TimeEntry = only is null or Destination.TimeEntry;
        panel.KbArticle = only is null or Destination.KbArticle;
        await panel.PublishAsync(draft ?? Draft(), []);
        return panel;
    }
}
