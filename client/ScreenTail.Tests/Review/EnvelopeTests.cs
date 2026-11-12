using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The envelope header (Spec v0.6.0 §5 S3, Outbox): four labelled lines over the note, each a value read
/// in one pass, and Publish held until a ticket is bound with the reason written beside it. Worded in
/// <see cref="PublishPanel"/>, where it is tested without WPF.
/// </summary>
public sealed class EnvelopeTests
{
    private static readonly string[] Both = ["connectwise", "hudu"];
    private static readonly TicketMatch Acme = new("48213", "Printer offline", "Acme Dental");

    [Fact]
    public void WithNoTicketToSaysSoAndPublishWaitsWithItsReason()
    {
        var panel = Panel();

        Assert.Equal(PublishPanel.NoTicketYet, panel.ToValue);
        Assert.Equal("No ticket yet", panel.ToValue);
        Assert.Equal("Choose a ticket", panel.ChangeLabel);
        Assert.Null(panel.MatchedFrom);

        // The gate: disabled, and the reason is the spec's words, beside the button.
        Assert.False(panel.CanPublish);
        Assert.Equal("Choose a ticket first", panel.BlockedBecause);
    }

    [Fact]
    public void ABoundTicketIsAValueWithChangeAndPublishOpens()
    {
        var panel = Panel();
        panel.Choose(Acme);

        Assert.Equal("#48213 Printer offline · Acme Dental", panel.ToValue);
        Assert.Equal("Change", panel.ChangeLabel);
        Assert.True(panel.CanPublish);
        Assert.Null(panel.BlockedBecause);
    }

    [Fact]
    public void ASuggestedTicketSaysWhereItWasMatchedFromByTheToolsName()
    {
        var panel = Panel();
        panel.Suggest(Acme);

        Assert.Equal("Matched from the ScreenConnect window title", panel.MatchedFrom);
    }

    [Fact]
    public void AChosenTicketClaimsNoMatch()
    {
        var panel = Panel();
        panel.Suggest(Acme);
        panel.Choose(new TicketMatch("48190", "VPN drops hourly", "Acme Dental"));

        Assert.Null(panel.MatchedFrom);
    }

    [Fact]
    public void WithNoPsaThereIsNoTicketToChooseAndTheGateSaysConnect()
    {
        var panel = PublishPanel.Unconnected(Session(draft: Draft()));

        Assert.Equal("No ticket yet", panel.ToValue);
        Assert.Equal(PublishPanel.ConnectPrompt, panel.BlockedBecause);
        Assert.False(panel.CanPublish);
    }

    [Theory]
    [InlineData(12, "12 min, rounded up to 15")]
    [InlineData(15, "15 min")]
    [InlineData(31, "31 min, rounded up to 45")]
    public void TimeSaysWhatWasCapturedAndHowItWasRounded(long minutes, string expected)
    {
        var panel = Panel(draft: Draft() with { SuggestedTimeMinutes = minutes });

        Assert.Equal(expected, panel.TimeNote);
    }

    [Fact]
    public void AsDefaultsToInternal()
    {
        // v0.4.1 Q2, unchanged by the envelope: a first draft never defaults to customer-facing.
        Assert.Equal(NoteType.Internal, Panel().NoteType);
        Assert.Equal("Discussion: the customer sees it", PublishPanel.DiscussionLabel);
        Assert.Equal("Internal note", PublishPanel.InternalLabel);
    }

    [Fact]
    public void TheKbChipNamesTheDocumentationPlatformAndTheArticle()
    {
        var panel = Panel(draft: Draft() with { SuggestedTitle = "Restart a stopped Print Spooler" });

        Assert.Equal("KB article in Hudu: “Restart a stopped Print Spooler”", panel.KbChipLabel);
    }

    [Fact]
    public void TheKbChipSaysWhatItIsWhenNoPlatformIsConnected()
    {
        var panel = new PublishPanel(Session(draft: Draft()), ["connectwise"], (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]), (_, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([]));

        Assert.StartsWith("KB article in the knowledge base", panel.KbChipLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsStillOpenCountsInferredStepsNotChecked()
    {
        Assert.Null(PublishPanel.OpenItems(Draft(Step("Checked the spooler."))));
        Assert.Equal("1 inferred step not checked", PublishPanel.OpenItems(Draft(Step("a"), Step("b", StepConfidence.Low))));
        Assert.Equal("2 inferred steps not checked", PublishPanel.OpenItems(Draft(Step("a", StepConfidence.Low), Step("b", StepConfidence.Low))));

        // Looks right (Alt+C) checks it.
        Assert.Null(PublishPanel.OpenItems(Draft(Step("a", StepConfidence.Low) with { Confirmed = true })));
        Assert.Null(PublishPanel.OpenItems(null));
    }

    [Fact]
    public void TheEnvelopeCopyHasNoDashes()
    {
        var panel = Panel(draft: Draft() with { SuggestedTitle = "Restart a stopped Print Spooler" });
        panel.Suggest(Acme);
        string?[] copy = [panel.ToValue, panel.ChangeLabel, panel.MatchedFrom, panel.TimeNote, panel.KbChipLabel, PublishPanel.DiscussionLabel, PublishPanel.NoTicketYet];

        Assert.All(copy, line => Assert.DoesNotContain("—", line ?? string.Empty, StringComparison.Ordinal));
    }

    private static PublishPanel Panel(DraftNote? draft = null) => new(
        Session(draft: draft ?? Draft()),
        Both,
        (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
        (r, _) => Task.FromResult<IReadOnlyList<DestinationResult>>([.. r.Destinations.Select(d => new DestinationResult(d, true))]));
}
