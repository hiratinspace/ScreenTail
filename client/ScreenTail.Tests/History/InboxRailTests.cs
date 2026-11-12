using ScreenTail.Core.History;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.History;

/// <summary>
/// The inbox rail (Spec v0.6.0 §5 S4, as built v0.6.4): History grouped by what each session needs, each
/// row worded from what History's rows carry, and the search over the same words.
/// </summary>
public sealed class InboxRailTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("draft", false, 1, InboxGroup.ToCheck)]
    [InlineData("partial", false, 1, InboxGroup.ToCheck)]
    [InlineData("pending", false, 1, InboxGroup.ToCheck)]
    [InlineData("draft", true, 1, InboxGroup.NotConfirmed)]
    [InlineData("published", true, 30, InboxGroup.NotConfirmed)]
    [InlineData("published", false, 2, InboxGroup.PublishedToday)]
    [InlineData("published", false, 20, InboxGroup.Earlier)]
    [InlineData("discarded", false, 1, InboxGroup.Earlier)]
    [InlineData("discarded", true, 1, InboxGroup.Earlier)]
    [InlineData("archived", false, 1, InboxGroup.ToCheck)]
    public void EachSessionSitsInTheGroupThatSaysWhatItNeeds(string status, bool unconfirmed, double hoursAgo, InboxGroup group) =>
        Assert.Equal(group, InboxRail.GroupOf(Row("s", status, hoursAgo, unconfirmed), Now, Utc));

    [Fact]
    public void GroupsComeInTheSpecsOrderNewestFirstInEach()
    {
        var rows = new[]
        {
            Row("old-draft", "draft", 5),
            Row("published", "published", 1),
            Row("earlier", "published", 40),
            Row("doubt", "draft", 3, unconfirmed: true),
            Row("new-draft", "draft", 0.5),
        };

        var items = InboxRail.Items(rows, null, Now, Utc);

        Assert.Equal(["new-draft", "old-draft", "doubt", "published", "earlier"], items.Select(i => i.Row.Id));
    }

    [Theory]
    [InlineData("draft", InboxGroup.ToCheck, null)]
    [InlineData("partial", InboxGroup.ToCheck, "Partial capture")]
    [InlineData("pending", InboxGroup.ToCheck, "No draft yet")]
    [InlineData("draft", InboxGroup.NotConfirmed, null)]
    [InlineData("published", InboxGroup.PublishedToday, null)]
    [InlineData("published", InboxGroup.Earlier, "Published")]
    [InlineData("discarded", InboxGroup.Earlier, "Discarded")]
    public void TheBadgeSaysTheStateOnlyWhereTheGroupDoesNot(string status, InboxGroup group, string? badge) =>
        Assert.Equal(badge, InboxRail.Badge(Row("s", status, 1), group));

    [Theory]
    [InlineData(0.01, "Just now")]
    [InlineData(14.0 / 60, "14 min ago")]
    [InlineData(2.5, "2 h ago")]
    [InlineData(20, "Yesterday")]
    [InlineData(24 * 3, "Mon 9 Nov")]
    [InlineData(24 * 400, "8 Oct 2025")]
    public void AgeReadsTheWayAnInboxDoes(double hoursAgo, string age) =>
        Assert.Equal(age, InboxRail.Age(Now.AddHours(-hoursAgo), Now, Utc));

    [Theory]
    [InlineData(8_000, "Under 1 min")]
    [InlineData(12 * 60_000, "12 min")]
    [InlineData(65 * 60_000, "1 h 5 min")]
    public void DurationIsInMinutes(long ms, string words) => Assert.Equal(words, InboxRail.Duration(ms));

    [Fact]
    public void ARowSaysItsToolAgeDurationAndScreenshotsAndIsSpokenWithItsStart()
    {
        var item = InboxRail.Item(Row("s", "partial", 14.0 / 60, frames: 7, durationMs: 12 * 60_000), Now, Utc);

        Assert.Equal("ScreenConnect session", item.Title);
        Assert.Equal("14 min ago", item.Age);
        Assert.Equal("12 min · 7 screenshots", item.Meta);
        Assert.Equal("ScreenConnect session, Partial capture, 14 min ago, started Thu 12 Nov 14:46, 12 min · 7 screenshots", item.Spoken);
    }

    [Fact]
    public void OneScreenshotIsSingular() =>
        Assert.Equal("1 min · 1 screenshot", InboxRail.Meta(Row("s", "draft", 1, frames: 1, durationMs: 60_000)));

    [Theory]
    [InlineData("screenconnect", true)]
    [InlineData("Screen", true)]
    [InlineData("rdp", false)]
    [InlineData("to check", true)]
    [InlineData("draft", true)]
    [InlineData("today", true)]
    [InlineData("thu", true)]
    [InlineData("12 nov", true)]
    [InlineData("14:00", true)]
    [InlineData("published", false)]
    [InlineData("  ", true)]
    public void SearchLooksThroughTheWordsTheRowHas(string query, bool matches) =>
        Assert.Equal(matches, InboxRail.Matches(Row("s", "draft", 1), query, Now, Utc));

    [Fact]
    public void SearchFindsANotConfirmedSessionByThoseWords()
    {
        var rows = new[] { Row("doubt", "draft", 3, unconfirmed: true), Row("plain", "draft", 2) };

        Assert.Equal(["doubt"], InboxRail.Items(rows, "not confirmed", Now, Utc).Select(i => i.Row.Id));
    }

    [Fact]
    public void EveryWordOfTheSearchMustMatch()
    {
        var rows = new[] { Row("sc", "draft", 1), Row("rdp", "draft", 1, tool: "rdp") };

        Assert.Equal(["rdp"], InboxRail.Items(rows, "remote today", Now, Utc).Select(i => i.Row.Id));
        Assert.Empty(InboxRail.Items(rows, "remote yesterday", Now, Utc));
    }

    [Fact]
    public void NoMatchQuotesTheSearch() => Assert.Equal("No sessions match “anydesk”.", InboxRail.NoMatch(" anydesk "));

    [Theory]
    [InlineData(InboxGroup.ToCheck, 2, "To check, 2 sessions")]
    [InlineData(InboxGroup.NotConfirmed, 1, "Not confirmed, 1 session")]
    [InlineData(InboxGroup.Earlier, 31, "Earlier, 31 sessions")]
    public void AGroupIsHeardWithItsCount(InboxGroup group, int count, string heading) =>
        Assert.Equal(heading, InboxRail.Heading(group, count));

    [Fact]
    public void PublishedTodayGoesByTheZonesDay()
    {
        // 23:30 the evening before in UTC is half past midnight today an hour east.
        var row = Row("s", "published", 15.5);
        var east = TimeZoneInfo.CreateCustomTimeZone("east", TimeSpan.FromHours(1), "east", "east");

        Assert.Equal(InboxGroup.Earlier, InboxRail.GroupOf(row, Now, Utc));
        Assert.Equal(InboxGroup.PublishedToday, InboxRail.GroupOf(row, Now, east));
    }

    private static SessionRow Row(string id, string status, double hoursAgo, bool unconfirmed = false, int frames = 3, long durationMs = 540_000, string tool = "screenconnect") => new()
    {
        Id = id,
        StartedAt = Now.AddHours(-hoursAgo),
        DurationMs = durationMs,
        Status = status,
        Tool = tool,
        Frames = frames,
        FramesPurged = 0,
        PublishUnconfirmed = unconfirmed,
    };
}
