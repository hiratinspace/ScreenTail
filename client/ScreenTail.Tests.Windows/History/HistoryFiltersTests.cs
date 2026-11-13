using System.Runtime.Versioning;
using System.Windows;
using ScreenTail.Core.History;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Review;
using ScreenTail.UI.History;

namespace ScreenTail.Tests.Windows.History;

/// <summary>
/// All sessions' filters and badges (spec v0.6.9 §5 S4): the rows the filters keep, the line that says
/// how many, Clear filters, the tool list from the sessions there are, and each row's status in the
/// rail's word, glyph and hue. The rules are Core's (RailLabelsTests on the Mac); this is the screen's
/// model over them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HistoryFiltersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheTableOpensUnfilteredWithEveryToolTheSessionsHaveOnOffer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = Model(Rows());
            Assert.Equal("Asking the capture service…", model.Status);
            await model.RefreshAsync();

            Assert.Equal(4, model.Rows.Count);
            Assert.Equal("4 sessions", model.Status);
            Assert.False(model.Filter.IsActive);
            Assert.Equal(Visibility.Visible, model.FiltersVisibility);
            Assert.Equal(Visibility.Collapsed, model.ClearFiltersVisibility);
            Assert.Equal(["Any tool", "Remote Desktop", "ScreenConnect"], model.ToolChoices.Select(c => c.Label));
        });
    }

    [Fact]
    public void AFilterKeepsItsRowsSaysHowManyOfAllAndClearFiltersBringsThemBack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = Model(Rows());
            await model.RefreshAsync();

            model.StatusChoice = HistoryFilters.StatusChoices.Single(c => c.Value == "published");
            Assert.Equal(["s2"], model.Rows.Select(r => r.Id));
            Assert.Equal("1 of 4 sessions", model.Status);
            Assert.Equal(Visibility.Visible, model.ClearFiltersVisibility);

            model.PeriodChoice = HistoryFilters.PeriodChoices.Single(c => c.Value == HistoryPeriod.Today);
            Assert.Empty(model.Rows);
            Assert.Equal(HistoryFilters.NoMatch, model.Status);

            model.ClearFiltersCommand.Execute(null);
            Assert.Equal(4, model.Rows.Count);
            Assert.Equal("4 sessions", model.Status);
            Assert.Equal(Visibility.Collapsed, model.ClearFiltersVisibility);
        });
    }

    [Fact]
    public void TheChosenToolStaysChosenWhenTheListIsReadAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = Model(Rows());
            await model.RefreshAsync();
            model.ToolChoice = model.ToolChoices.Single(c => c.Value == "rdp");

            await model.RefreshAsync();

            Assert.Equal("rdp", model.ToolChoice?.Value);
            Assert.Equal(["s3"], model.Rows.Select(r => r.Id));
        });
    }

    [Fact]
    public void WithNoSessionsThereAreNoFiltersOnlyTheEmptyStatesSentence()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = Model([]);
            await model.RefreshAsync();

            Assert.Equal(SessionHistory.Empty, model.Status);
            Assert.Equal(Visibility.Collapsed, model.FiltersVisibility);
        });
    }

    [Fact]
    public void EachRowsStatusIsTheRailsWordGlyphAndHue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = Model(Rows());
            await model.RefreshAsync();

            foreach (var row in model.Rows)
            {
                var status = Rows().Single(r => r.Id == row.Id).Status;
                var badge = StatusBadges.Of(status);
                Assert.Equal(badge.Word, row.Status);
                Assert.Equal(badge.Glyph, row.StatusGlyph);
                Assert.Equal(badge.Tone, row.StatusTone);
                Assert.False(string.IsNullOrEmpty(row.StatusGlyph));
            }

            Assert.Equal("Partial capture", model.Rows.Single(r => r.Id == "s4").Status);
            Assert.Equal(StatusBadges.NotConfirmed.Glyph, HistoryRow.NotConfirmedGlyph);
        });
    }

    private static HistoryViewModel Model(IReadOnlyList<SessionRow> rows) =>
        new(new ShellState(), _ => Task.FromResult<IReadOnlyList<SessionRow>?>(rows), time: new Fixed(Now), zone: TimeZoneInfo.Utc);

    private static IReadOnlyList<SessionRow> Rows() =>
    [
        Row("s1", "draft", "screenconnect", hoursAgo: 1),
        Row("s2", "published", "screenconnect", hoursAgo: 30),
        Row("s3", "draft", "rdp", hoursAgo: 2),
        Row("s4", "partial", "screenconnect", hoursAgo: 3),
    ];

    private static SessionRow Row(string id, string status, string tool, double hoursAgo) => new()
    {
        Id = id,
        StartedAt = Now.AddHours(-hoursAgo),
        DurationMs = 60_000,
        Status = status,
        Tool = tool,
        Frames = 1,
        FramesPurged = 0,
    };

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
