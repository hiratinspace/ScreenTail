using ScreenTail.Core.History;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.History;

/// <summary>
/// Spec v0.6.9 §5 S4 (as built): the rail's rows say the company, the note's title and the ticket when
/// the service sends them (contract v11), and a row without them reads as it did before. A value that is
/// not known is left out, never a placeholder. The status badges are one rule for the rail and the All
/// sessions table: a word, a glyph and a hue, never colour alone. All sessions filters by status, tool
/// and period. The rail narrows at small windows.
/// </summary>
public sealed class RailLabelsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void ARowWithCompanyTitleAndTicketSaysThemInTheTargetsOrder()
    {
        var item = InboxRail.Item(Row(company: "Acme Dental", title: "Printer offline", ticket: "48213"), Now, Utc);

        Assert.Equal("Acme Dental", item.Title);
        Assert.Equal("Printer offline", item.Subtitle);
        Assert.Equal("#48213 · ScreenConnect · 9 min", item.Meta);
        Assert.Equal("1 h ago", item.Age);
        Assert.Equal("Acme Dental, Printer offline, 1 h ago, started Thu 12 Nov 14:00, ticket 48213, ScreenConnect, 9 min · 3 screenshots", item.Spoken);
    }

    [Fact]
    public void ARowWithATitleAndNoCompanyIsHeadedByTheTitle()
    {
        var item = InboxRail.Item(Row(title: "Printer offline"), Now, Utc);

        Assert.Equal("Printer offline", item.Title);
        Assert.Null(item.Subtitle);
        Assert.Equal("ScreenConnect · 9 min", item.Meta);
    }

    [Fact]
    public void ARowWithOnlyATicketKeepsTheToolAsItsHeading()
    {
        var item = InboxRail.Item(Row(ticket: "48213"), Now, Utc);

        Assert.Equal("ScreenConnect session", item.Title);
        Assert.Null(item.Subtitle);
        Assert.Equal("#48213 · 9 min · 3 screenshots", item.Meta);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", " ", "  ")]
    public void ARowWithoutThemReadsAsItDidBefore(string? company, string? title, string? ticket)
    {
        // An older service, or a session nothing is known about yet: no "No ticket yet", no "Unknown company".
        var item = InboxRail.Item(Row(company: company, title: title, ticket: ticket), Now, Utc);

        Assert.Equal("ScreenConnect session", item.Title);
        Assert.Null(item.Subtitle);
        Assert.Equal("9 min · 3 screenshots", item.Meta);
        Assert.Equal("ScreenConnect session, 1 h ago, started Thu 12 Nov 14:00, 9 min · 3 screenshots", item.Spoken);
    }

    [Fact]
    public void ACompanyWithNoTitleIsHeadingAlone()
    {
        var item = InboxRail.Item(Row(company: "Acme Dental"), Now, Utc);

        Assert.Equal("Acme Dental", item.Title);
        Assert.Null(item.Subtitle);
        Assert.Equal("ScreenConnect · 9 min", item.Meta);
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("printer offline")]
    [InlineData("48213")]
    [InlineData("#48213")]
    [InlineData("acme 482")]
    public void TheSearchLooksThroughCompanyTitleAndTicket(string query) =>
        Assert.True(InboxRail.Matches(Row(company: "Acme Dental", title: "Printer offline", ticket: "48213"), query, Now, Utc));

    [Fact]
    public void TheSearchDoesNotMatchWordsARowDoesNotHave() =>
        Assert.False(InboxRail.Matches(Row(company: "Acme Dental", title: "Printer offline", ticket: "48213"), "contoso", Now, Utc));

    [Fact]
    public void TheHintNamesWhatTheRowsNowCarry()
    {
        Assert.Equal("Company, ticket or title", InboxRail.SearchHint);
        Assert.Contains("tool", InboxRail.SearchHelp, StringComparison.Ordinal);
        Assert.Contains("Esc", InboxRail.SearchHelp, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("draft", "Draft", "Accent")]
    [InlineData("published", "Published", "Published")]
    [InlineData("partial", "Partial capture", "Paused")]
    [InlineData("pending", "No draft yet", "Offline")]
    [InlineData("discarded", "Discarded", "Muted")]
    [InlineData("archived", "Archived", "Muted")]
    public void EveryStatusHasAWordAGlyphAndAHue(string status, string word, string tone)
    {
        var badge = StatusBadges.Of(status);

        Assert.Equal(word, badge.Word);
        Assert.Equal(tone, badge.Tone);
        Assert.False(string.IsNullOrEmpty(badge.Glyph));
    }

    [Fact]
    public void NoTwoStatusesShareAGlyphSoTheShapeAloneTellsThemApart()
    {
        var glyphs = new[] { "draft", "published", "partial", "pending", "discarded" }
            .Select(s => StatusBadges.Of(s).Glyph)
            .Append(StatusBadges.NotConfirmed.Glyph)
            .ToList();

        Assert.Equal(glyphs.Count, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NotConfirmedHasItsOwnGlyphBesideTheWarningHue()
    {
        Assert.Equal(SessionHistory.NotConfirmedLabel, StatusBadges.NotConfirmed.Word);
        Assert.Equal("Paused", StatusBadges.NotConfirmed.Tone);
        Assert.False(string.IsNullOrEmpty(StatusBadges.NotConfirmed.Glyph));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("pending")]
    [InlineData("discarded")]
    [InlineData("published")]
    public void TheRailSaysAStateInTheTablesWords(string status) =>
        Assert.Equal(StatusBadges.Of(status).Word, InboxRail.Badge(Row(status: status, hoursAgo: 72), InboxGroup.Earlier));

    [Fact]
    public void TheFiltersStartOpenAndKeepEveryRow()
    {
        var rows = Rows();

        Assert.Equal(rows.Select(r => r.Id), HistoryFilters.Apply(rows, HistoryFilter.Any, Now, Utc).Select(r => r.Id));
        Assert.False(HistoryFilter.Any.IsActive);
    }

    [Fact]
    public void TheStatusFilterKeepsThatStatusOnly() =>
        Assert.Equal(["partial"], HistoryFilters.Apply(Rows(), HistoryFilter.Any with { Status = "partial" }, Now, Utc).Select(r => r.Id));

    [Fact]
    public void TheToolFilterKeepsThatToolOnly() =>
        Assert.Equal(["rdp"], HistoryFilters.Apply(Rows(), HistoryFilter.Any with { Tool = "rdp" }, Now, Utc).Select(r => r.Id));

    [Theory]
    [InlineData(HistoryPeriod.Today, new[] { "today", "partial", "rdp" })]
    [InlineData(HistoryPeriod.Last7Days, new[] { "today", "partial", "rdp", "week" })]
    [InlineData(HistoryPeriod.Last30Days, new[] { "today", "partial", "rdp", "week", "month" })]
    [InlineData(HistoryPeriod.AnyTime, new[] { "today", "partial", "rdp", "week", "month", "old" })]
    public void ThePeriodCountsWholeDaysInTheTechniciansZone(HistoryPeriod period, string[] kept)
    {
        var filter = HistoryFilter.Any with { Period = period };

        Assert.Equal(kept, HistoryFilters.Apply(Rows(), filter, Now, Utc).Select(r => r.Id));
        Assert.Equal(period != HistoryPeriod.AnyTime, filter.IsActive);
    }

    [Fact]
    public void TheFiltersOfferEveryStatusInTheBadgesWordsAndTheToolsTheRowsHave()
    {
        Assert.Equal(["Any status", "Draft", "No draft yet", "Partial capture", "Published", "Discarded"], HistoryFilters.StatusChoices.Select(c => c.Label));
        Assert.Equal(["Any tool", "Remote Desktop", "ScreenConnect"], HistoryFilters.ToolChoices(Rows()).Select(c => c.Label));
        Assert.Equal(["Any time", "Today", "Last 7 days", "Last 30 days"], HistoryFilters.PeriodChoices.Select(c => c.Label));
    }

    [Fact]
    public void FiltersThatLeaveNothingSaySoInPlaceOfTheTable() =>
        Assert.Equal("No sessions match these filters.", HistoryFilters.NoMatch);

    [Theory]
    [InlineData(1024, false)]
    [InlineData(1179, false)]
    [InlineData(1180, true)]
    [InlineData(1920, true)]
    public void TheRailIsNarrowInASmallWindowAndFullWidthOtherwise(double windowWidth, bool wide)
    {
        Assert.Equal(wide, RailLayout.IsWide(windowWidth, breakpoint: 1180));
    }

    private static IReadOnlyList<SessionRow> Rows() =>
    [
        Row(id: "today", hoursAgo: 1),
        Row(id: "partial", status: "partial", hoursAgo: 2),
        Row(id: "rdp", tool: "rdp", hoursAgo: 3),
        Row(id: "week", hoursAgo: 24 * 5),
        Row(id: "month", hoursAgo: 24 * 20),
        Row(id: "old", hoursAgo: 24 * 60),
    ];

    private static SessionRow Row(
        string id = "s",
        string status = "draft",
        double hoursAgo = 1,
        string tool = "screenconnect",
        string? company = null,
        string? title = null,
        string? ticket = null) => new()
    {
        Id = id,
        StartedAt = Now.AddHours(-hoursAgo),
        DurationMs = 540_000,
        Status = status,
        Tool = tool,
        Frames = 3,
        FramesPurged = 0,
        Company = company,
        Title = title,
        Ticket = ticket,
    };
}
