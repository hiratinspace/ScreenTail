using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The result list after a publish (Spec §5 S3): each row names its destination's provider in its link,
/// says its state in a word as well as a glyph, and a reason is said once, where the technician can act
/// on it. Retry says what it sends again.
/// </summary>
public sealed class PublishResultRowsTests
{
    private static readonly string[] Both = ["connectwise", "hudu"];

    private static readonly Uri Ticket = new("https://na.myconnectwise.net/ticket/48213");
    private static readonly Uri Article = new("https://docs.example.huducloud.com/kba/1");

    private const string Unmapped = "\"Acme Dental\" is not mapped to a company in the documentation platform. Choose one below, or map it in Settings → Integrations.";

    [Fact]
    public async Task EachLinkIsNamedForTheProviderItGoesTo()
    {
        // The KB article goes to the documentation platform, not the PSA: "Open in ConnectWise" on it
        // sent the technician looking in the wrong product.
        var panel = await PublishedAsync(Both, d => new DestinationResult(d, true, d == Destination.KbArticle ? Article : Ticket));

        var rows = panel.Rows.ToDictionary(r => r.Destination);

        Assert.Equal("Open in ConnectWise", rows[Destination.TicketNote].LinkText);
        Assert.Equal("Open in ConnectWise", rows[Destination.TimeEntry].LinkText);
        Assert.Equal("Open in Hudu", rows[Destination.KbArticle].LinkText);
        Assert.Equal("Open in Hudu: KB article", rows[Destination.KbArticle].LinkName);
    }

    [Fact]
    public async Task TheLinkNameComesFromTheConnectedProvidersNotFromTheCode()
    {
        // The demo's providers are not ConnectWise or Hudu, and their links must not say they are.
        var panel = await PublishedAsync(["demo-psa", "demo-kb"], d => new DestinationResult(d, true, Ticket));

        var rows = panel.Rows.ToDictionary(r => r.Destination);

        Assert.Equal("Open in the demo PSA", rows[Destination.TicketNote].LinkText);
        Assert.Equal("Open in the demo knowledge base", rows[Destination.KbArticle].LinkText);
    }

    [Fact]
    public async Task AProviderThisVersionDoesNotKnowIsNotGivenARealProductsName()
    {
        // Only ConnectWise is connected, so nothing says which product the article went to.
        var panel = await PublishedAsync(["connectwise"], d => new DestinationResult(d, true, Article));

        Assert.Equal("Open in the knowledge base", panel.Rows.Single(r => r.Destination == Destination.KbArticle).LinkText);
    }

    [Theory]
    [InlineData(true, null, ResultState.Sent, "✓", "Sent", "Published")]
    [InlineData(false, null, ResultState.Failed, "✗", "Failed", "Failed")]
    [InlineData(false, DestinationResult.Unknown, ResultState.NotConfirmed, "?", "Not confirmed", "Not confirmed")]
    [InlineData(false, DestinationResult.TimedOut, ResultState.NotConfirmed, "?", "Not confirmed", "Not confirmed")]
    [InlineData(false, DestinationResult.AlreadyPublished, ResultState.AlreadyPublished, "?", "Already published", "Already published")]
    public async Task EachRowSaysItsStateInAWordBesideTheGlyph(bool ok, string? kind, ResultState state, string mark, string badge, string spoken)
    {
        // The glyph is drawn in its state's colour, and the badge carries the word, so the state is never
        // a colour alone. The glyph's spoken name is kept: a screen reader would say "question mark".
        var panel = await PublishedAsync(Both, d => new DestinationResult(d, ok, ok ? Ticket : null, ok ? null : "no answer", kind), only: Destination.TicketNote);

        var row = Assert.Single(panel.Rows);

        Assert.Equal(state, row.State);
        Assert.Equal(mark, row.Mark);
        Assert.Equal(badge, row.Badge);
        Assert.Equal(spoken, row.MarkName);
    }

    [Fact]
    public async Task TheMissingMappingIsExplainedOnceInThePromptThatFixesIt()
    {
        // It was said three times: in the summary, on the row and in the prompt. The prompt is the one
        // with the choice in it, so the reason lives there; the summary says what to do, the row says Failed.
        var panel = await NeedsMappingAsync();

        var article = panel.Rows.Single(r => r.Destination == Destination.KbArticle);

        Assert.True(panel.NeedsMapping);
        Assert.Equal(ResultState.Failed, article.State);
        Assert.Null(article.Error);
        Assert.Equal("Note published. KB article not sent yet: choose its company below", panel.Summary);
        Assert.DoesNotContain("not mapped", panel.Summary, StringComparison.Ordinal);
        Assert.Equal("“Acme Dental” is not mapped to a company in Hudu yet. Choose the one it is and the article goes there. The choice is remembered.", panel.MappingPrompt);
    }

    [Fact]
    public async Task WithoutAMappingPromptTheRowKeepsTheBackendsWords()
    {
        // No prompt to say it in: the row is then the only place the reason can be read.
        var panel = await NeedsMappingAsync(withMapping: false);

        Assert.False(panel.NeedsMapping);
        Assert.Null(panel.MappingPrompt);
        Assert.Equal(Unmapped, panel.Rows.Single(r => r.Destination == Destination.KbArticle).Error);
    }

    [Fact]
    public async Task RetryNamesWhereItSendsAgain()
    {
        var panel = await PublishedAsync(Both, d => d == Destination.KbArticle
            ? new DestinationResult(d, false, null, "Hudu did not answer")
            : new DestinationResult(d, true, Ticket));

        Assert.Equal("Retry Hudu", panel.RetryLabel);
        Assert.Equal("Retry Hudu: the KB article", panel.RetryName);
    }

    [Fact]
    public async Task RetryNamesEachProviderOnceWhenBothFailed()
    {
        var panel = await PublishedAsync(Both, d => d == Destination.TicketNote
            ? new DestinationResult(d, true, Ticket)
            : new DestinationResult(d, false, null, "no answer"));

        Assert.Equal("Retry ConnectWise and Hudu", panel.RetryLabel);
        Assert.Equal("Retry ConnectWise and Hudu: the time entry and the KB article", panel.RetryName);
    }

    [Fact]
    public void NoCopyHereUsesAnEmDash()
    {
        // The spec's voice since v0.5.0: a colon where a dash was.
        string[] copy = [PublishPanel.CheckTheTicketFirst, PublishPanel.PartlyPublished, PublishPanel.AlreadyPublished, PublishPanel.ConnectPrompt];

        Assert.All(copy, line => Assert.DoesNotContain("—", line, StringComparison.Ordinal));
    }

    private static async Task<PublishPanel> NeedsMappingAsync(bool withMapping = true)
    {
        var panel = new PublishPanel(
            Session(draft: Draft()),
            Both,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => d == Destination.KbArticle
                ? new DestinationResult(d, false, null, Unmapped, DestinationResult.NeedsMapping)
                : new DestinationResult(d, true, Ticket))]),
            mapping: withMapping ? new CompanyMapping(_ => Task.FromResult<IReadOnlyList<CompanyChoice>>([]), (_, _, _) => Task.FromResult(true)) : null);
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.KbArticle = true;
        await panel.PublishAsync(Draft(), []);
        return panel;
    }

    private static async Task<PublishPanel> PublishedAsync(IReadOnlyList<string> integrations, Func<Destination, DestinationResult> result, Destination? only = null)
    {
        var panel = new PublishPanel(
            Session(draft: Draft()),
            integrations,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(result)]));
        panel.Choose(new TicketMatch("48213", "Printer offline", "Acme Dental"));
        panel.TicketNote = only is null or Destination.TicketNote;
        panel.TimeEntry = only is null or Destination.TimeEntry;
        panel.KbArticle = only is null or Destination.KbArticle;
        await panel.PublishAsync(Draft(), []);
        return panel;
    }
}
