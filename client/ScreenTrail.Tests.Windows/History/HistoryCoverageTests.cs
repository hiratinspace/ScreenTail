using System.Runtime.Versioning;
using System.Windows;
using ScreenTrail.Core.History;
using ScreenTrail.Core.Shell;
using ScreenTrail.Shared.Ipc;
using ScreenTrail.Tests.Windows.Review;
using ScreenTrail.UI.History;

namespace ScreenTrail.Tests.Windows.History;

/// <summary>
/// The coverage banner on All sessions (ST-079, spec v0.7.3 S4): asked for the period the filters chose,
/// summed over the tool chosen, hidden under a status filter, and dropped when its answer is for a period
/// no longer chosen. The words are Core's (CoverageBannerTests on the Mac); this is the screen's model.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HistoryCoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheBannerDescribesTheRowsTheFiltersLeave()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var asked = new List<DateTimeOffset?>();
            var model = Model(asked);
            await model.RefreshAsync();

            // Any time: everything the device counted, since it began counting.
            Assert.Equal([null], asked);
            Assert.Equal(Visibility.Visible, model.CoverageVisibility);
            Assert.Equal(Visibility.Visible, model.CoverageDetailVisibility);
            Assert.StartsWith("Captured 5 of 8 remote sessions since ", model.CoverageHeadline, StringComparison.Ordinal);
            Assert.Equal("3 not captured: auto-start off 2, tool switched off 1.", model.CoverageDetail);
            Assert.Equal($"{model.CoverageHeadline} {model.CoverageDetail}", model.CoverageSpoken);
            Assert.NotEmpty(model.CoverageHelp);

            // The tool filter sums that tool's counts only, with no new question.
            model.ToolChoice = model.ToolChoices.Single(c => c.Value == "rdp");
            Assert.Single(asked);
            Assert.StartsWith("Captured all 2 Remote Desktop sessions", model.CoverageHeadline, StringComparison.Ordinal);
            Assert.Equal(Visibility.Collapsed, model.CoverageDetailVisibility);

            // Another period asks again, from its first day.
            model.ToolChoice = model.ToolChoices[0];
            model.PeriodChoice = HistoryFilters.PeriodChoices.Single(c => c.Value == HistoryPeriod.Last7Days);
            await WaitForAsync(() => asked.Count == 2);
            Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), asked[1]);
            Assert.Equal("Captured 5 of 8 remote sessions in the last 7 days (63%).", model.CoverageHeadline);

            // A status filter hides it: a missed session has no status.
            model.StatusChoice = HistoryFilters.StatusChoices.Single(c => c.Value == "draft");
            Assert.Equal(Visibility.Collapsed, model.CoverageVisibility);
            model.ClearFiltersCommand.Execute(null);
            await WaitForAsync(() => asked.Count == 3);
            Assert.Equal(Visibility.Visible, model.CoverageVisibility);
        });
    }

    [Fact]
    public void NoAnswerIsNoBanner()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = new HistoryViewModel(
                new ShellState(),
                _ => Task.FromResult<IReadOnlyList<SessionRow>?>(Rows()),
                time: new Fixed(Now),
                zone: TimeZoneInfo.Utc,
                coverage: (_, _) => Task.FromResult<CoverageReported?>(null));
            await model.RefreshAsync();

            Assert.Equal(Visibility.Collapsed, model.CoverageVisibility);
            Assert.Empty(model.CoverageHeadline);
        });
    }

    [Fact]
    public void AnAnswerForAPeriodNoLongerChosenIsNotShown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var slow = new TaskCompletionSource<CoverageReported?>();
            var calls = 0;
            var model = new HistoryViewModel(
                new ShellState(),
                _ => Task.FromResult<IReadOnlyList<SessionRow>?>(Rows()),
                time: new Fixed(Now),
                zone: TimeZoneInfo.Utc,
                coverage: (from, _) => ++calls == 2 ? slow.Task : Task.FromResult<CoverageReported?>(Reported(from)));
            await model.RefreshAsync();

            // Today is asked and answers late; Last 7 days is asked after it and answers first.
            model.PeriodChoice = HistoryFilters.PeriodChoices.Single(c => c.Value == HistoryPeriod.Today);
            model.PeriodChoice = HistoryFilters.PeriodChoices.Single(c => c.Value == HistoryPeriod.Last7Days);
            await WaitForAsync(() => model.CoverageHeadline.Contains("last 7 days", StringComparison.Ordinal));
            slow.SetResult(Reported(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero)));
            await Task.Yield();

            Assert.Contains("last 7 days", model.CoverageHeadline, StringComparison.Ordinal);
        });
    }

    private static HistoryViewModel Model(List<DateTimeOffset?> asked) =>
        new(
            new ShellState(),
            _ => Task.FromResult<IReadOnlyList<SessionRow>?>(Rows()),
            time: new Fixed(Now),
            zone: TimeZoneInfo.Utc,
            coverage: (from, _) =>
            {
                asked.Add(from);
                return Task.FromResult<CoverageReported?>(Reported(from));
            });

    /// <summary>Counted from the period's start, or for any time from a week back.</summary>
    private static CoverageReported Reported(DateTimeOffset? from) => new()
    {
        CountedFrom = from ?? Now.AddDays(-7),
        RetentionDays = 7,
        Tools =
        [
            new() { Tool = "rdp", Captured = 2, Missed = [] },
            new() { Tool = "screenconnect", Captured = 3, Missed = [new() { Reason = "auto_start_off", Count = 2 }, new() { Reason = "tool_switched_off", Count = 1 }] },
        ],
    };

    private static IReadOnlyList<SessionRow> Rows() =>
    [
        new() { Id = "s1", StartedAt = Now.AddHours(-1), DurationMs = 60_000, Status = "draft", Tool = "screenconnect", Frames = 1, FramesPurged = 0 },
        new() { Id = "s2", StartedAt = Now.AddHours(-30), DurationMs = 60_000, Status = "published", Tool = "rdp", Frames = 1, FramesPurged = 0 },
    ];

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(done());
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
