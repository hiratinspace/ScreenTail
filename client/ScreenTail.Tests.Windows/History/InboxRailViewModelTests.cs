using System.Runtime.Versioning;
using System.Windows;
using ScreenTail.Core.History;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Review;
using ScreenTail.UI.History;

namespace ScreenTail.Tests.Windows.History;

/// <summary>
/// The inbox rail's model (Spec v0.6.0 §5 S4, as built v0.6.4): it fills the groups from History's rows,
/// keeps a row that stays when the list is read again (so focus on it stays), opens a row through the
/// store, draws the open session's row as the tab, and says what is going on when there is nothing to
/// list. The grouping and the words are InboxRailTests on the Mac.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InboxRailViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheGroupsFillWithTheirCountsAndEarlierStartsCollapsed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, _, _) = Rail(Row("a", "draft", 1), Row("b", "draft", 2, unconfirmed: true), Row("c", "published", 1), Row("d", "published", 40), Row("e", "discarded", 3));
            await model.RefreshAsync();

            Assert.Equal([1, 1, 1, 2], model.Groups.Select(g => g.Count));
            Assert.Equal(["To check, 1 session", "Not confirmed, 1 session", "Published today, 1 session", "Earlier, 2 sessions"], model.Groups.Select(g => g.Heading));
            var earlier = model.Groups[3];
            Assert.False(earlier.IsExpanded);
            Assert.Equal(Visibility.Collapsed, earlier.RowsVisibility);
            Assert.Equal(Visibility.Visible, earlier.Visibility);
            Assert.Equal(string.Empty, model.Notice);
        });
    }

    [Fact]
    public void AnEmptyGroupIsHidden()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, _, _) = Rail(Row("a", "draft", 1));
            await model.RefreshAsync();

            Assert.Equal([Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed], model.Groups.Select(g => g.Visibility));
        });
    }

    [Fact]
    public void ReadingTheListAgainKeepsEachRowThatStays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, _, rows) = Rail(Row("a", "draft", 1), Row("b", "draft", 2));
            await model.RefreshAsync();
            var kept = model.Rows.Single(r => r.Id == "b");

            rows.Insert(0, Row("new", "draft", 0.5));
            rows.RemoveAll(r => r.Id == "a");
            await model.RefreshAsync();

            Assert.Equal(["new", "b"], model.Rows.Select(r => r.Id));
            Assert.Same(kept, model.Rows.Single(r => r.Id == "b"));
        });
    }

    [Fact]
    public void ARowMovingGroupKeepsItsWords()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, _, rows) = Rail(Row("a", "draft", 1));
            await model.RefreshAsync();

            rows[0] = Row("a", "published", 1);
            await model.RefreshAsync();

            Assert.Empty(model.Groups[0].Rows);
            Assert.Equal("a", Assert.Single(model.Groups[2].Rows).Id);
        });
    }

    [Fact]
    public void OpeningARowOpensItsSessionThroughTheStoreAndTheRowBecomesTheTab()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, state, _) = Rail(Row("a", "draft", 1), Row("b", "draft", 2));
            await model.RefreshAsync();

            model.Open(model.Rows.Single(r => r.Id == "b"));

            Assert.Equal("b", state.Snapshot.Reviewing);
            Assert.Equal(ShellView.Review, state.Snapshot.View);

            model.Sync("b");
            Assert.Equal([false, true], model.Rows.Select(r => r.IsOpen));

            // Another view: no row is the tab.
            model.Sync(null);
            Assert.All(model.Rows, r => Assert.False(r.IsOpen));
        });
    }

    [Fact]
    public void TheOpenSessionsRowIsNeverHiddenInACollapsedGroup()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, _, _) = Rail(Row("old", "published", 40));
            await model.RefreshAsync();
            Assert.False(model.Groups[3].IsExpanded);

            model.Sync("old");

            Assert.True(model.Groups[3].IsExpanded);
        });
    }

    [Fact]
    public void ASearchThatMatchesNothingSaysSoAndOffersToClearIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var (model, _, _) = Rail(Row("a", "draft", 1), Row("old", "published", 40));
            await model.RefreshAsync();

            model.Query = "anydesk";
            Assert.Empty(model.Rows);
            Assert.Equal("No sessions match “anydesk”.", model.Notice);
            Assert.Equal(Visibility.Visible, model.ClearSearchVisibility);
            Assert.Equal(Visibility.Collapsed, model.SearchHintVisibility);

            model.ClearSearchCommand.Execute(null);
            Assert.Equal(2, model.Rows.Count());
            Assert.Equal(string.Empty, model.Notice);
            Assert.Equal(Visibility.Collapsed, model.ClearSearchVisibility);

            // A search opens Earlier, so a match there is not left behind its heading.
            model.Query = "published";
            Assert.True(model.Groups[3].IsExpanded);
        });
    }

    [Fact]
    public void NoSessionsSaysHowToMakeOneAndNoAnswerSaysSo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var state = new ShellState();
            var empty = new InboxRailViewModel(state, _ => Task.FromResult<IReadOnlyList<SessionRow>?>([]), new FixedTime(Now), TimeZoneInfo.Utc);
            Assert.Equal(InboxRail.Asking, empty.Notice);
            await empty.RefreshAsync();
            Assert.Equal(SessionHistory.Empty, empty.Notice);

            var silent = new InboxRailViewModel(state, _ => Task.FromResult<IReadOnlyList<SessionRow>?>(null), new FixedTime(Now), TimeZoneInfo.Utc);
            await silent.RefreshAsync();
            Assert.Equal(InboxRail.NotAnswered, silent.Notice);
        });
    }

    private static (InboxRailViewModel Model, ShellState State, List<SessionRow> Rows) Rail(params SessionRow[] rows)
    {
        var state = new ShellState();
        var list = rows.ToList();
        var model = new InboxRailViewModel(state, _ => Task.FromResult<IReadOnlyList<SessionRow>?>([.. list]), new FixedTime(Now), TimeZoneInfo.Utc);
        return (model, state, list);
    }

    private static SessionRow Row(string id, string status, double hoursAgo, bool unconfirmed = false) => new()
    {
        Id = id,
        StartedAt = Now.AddHours(-hoursAgo),
        DurationMs = 540_000,
        Status = status,
        Tool = "screenconnect",
        Frames = 3,
        FramesPurged = 0,
        PublishUnconfirmed = unconfirmed,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
