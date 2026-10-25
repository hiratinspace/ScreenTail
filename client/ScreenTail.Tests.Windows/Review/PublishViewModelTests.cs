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

    private static async Task<NoteType> PublishedAsAsync(Action<PublishViewModel> choose)
    {
        var sent = new List<PublishRequest>();
        var pane = Pane(sent);
        choose(pane);
        await pane.PublishCommand.ExecuteAsync(null);
        return Assert.Single(sent).NoteType;
    }

    private static PublishViewModel Pane(List<PublishRequest> sent)
    {
        var session = Session(Frames(1));
        var panel = new PublishPanel(
            session,
            ConnectWise,
            (_, _) => Task.FromResult<IReadOnlyList<TicketMatch>>([]),
            (request, _) =>
            {
                sent.Add(request);
                return Task.FromResult<IReadOnlyList<DestinationResult>>([.. request.Destinations.Select(d => new DestinationResult(d, true))]);
            });
        var pane = new PublishViewModel(panel, () => session.Draft!, () => ["f1"]);
        pane.SelectedMatch = new TicketMatch("48213", "Printer offline", "Acme Dental");
        return pane;
    }
}
