using System.Globalization;
using ScreenTrail.Core.History;
using ScreenTrail.Shared.Ipc;

namespace ScreenTrail.Tests.History;

/// <summary>
/// ST-079's coverage banner (spec v0.7.3 S4): the numbers over the filters the table has, with the true
/// denominator, and the words for each choice made under the owner's rule.
/// </summary>
public sealed class CoverageBannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public CoverageBannerTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Fact]
    public void TheDenominatorIsWhatWasSeenNotWhatWasCaptured()
    {
        // The spec's own example: 46 captured, 6 seen and missed.
        var coverage = SessionHistory.CoverageOf(Reported(Week(), Tool("screenconnect", 30, ("auto_start_off", 4)), Tool("rdp", 16, ("tool_switched_off", 2))));

        Assert.Equal((46, 52, 88), (coverage.Captured, coverage.Total, coverage.Percent));
        Assert.Equal([(MissReason.AutoStartOff, 4), (MissReason.ToolSwitchedOff, 2)], coverage.Missed);
    }

    [Fact]
    public void TheBannerSaysTheSpecsSentenceAndWhyTheMissesWereMissed()
    {
        var words = Banner(Reported(Week(), Tool("screenconnect", 30, ("auto_start_off", 4)), Tool("rdp", 16, ("tool_switched_off", 2))), HistoryPeriod.Last7Days)!;

        Assert.Equal("Captured 46 of 52 remote sessions in the last 7 days (88%).", words.Headline);
        Assert.Equal("6 not captured: auto-start off 4, tool switched off 2.", words.Detail);
        Assert.Equal($"{words.Headline} {words.Detail}", words.Spoken);
        Assert.Contains("7 days", words.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolFilterCountsThatToolOnlyAndSaysItsName()
    {
        var reported = Reported(Today(), Tool("screenconnect", 4, ("auto_start_off", 1)), Tool("rdp", 9, ("start_held", 3)));
        var words = Banner(reported, HistoryPeriod.Today, tool: "screenconnect")!;

        Assert.Equal("Captured 4 of 5 ScreenConnect sessions today (80%).", words.Headline);
        Assert.Equal("1 not captured: auto-start off 1.", words.Detail);
    }

    [Fact]
    public void EverythingCapturedSaysSoWithNoPercentOrReasons()
    {
        var words = Banner(Reported(Today(), Tool("rdp", 12)), HistoryPeriod.Today)!;
        Assert.Equal("Captured all 12 remote sessions today.", words.Headline);
        Assert.Empty(words.Detail);
        Assert.Equal(words.Headline, words.Spoken);

        Assert.Equal("Captured the only remote session today.", Banner(Reported(Today(), Tool("rdp", 1)), HistoryPeriod.Today)!.Headline);
    }

    [Fact]
    public void NothingCapturedStillSaysWhatWasMissed()
    {
        // Auto-start off all week: the table may be empty, and the banner says why.
        var words = Banner(Reported(Week(), Tool("anydesk", 0, ("auto_start_off", 5))), HistoryPeriod.Last7Days)!;
        Assert.Equal("Captured 0 of 5 remote sessions in the last 7 days (0%).", words.Headline);
        Assert.Equal("5 not captured: auto-start off 5.", words.Detail);
    }

    [Fact]
    public void NothingToCountIsNoBannerRatherThanZeroOfZero()
    {
        Assert.Null(Banner(Reported(Week()), HistoryPeriod.Last7Days));
        Assert.Null(Banner(Reported(Week(), Tool("rdp", 0)), HistoryPeriod.Last7Days));
        Assert.Null(Banner(Reported(Week(), Tool("rdp", 3)), HistoryPeriod.Last7Days, tool: "anydesk"));
    }

    [Fact]
    public void AStatusFilterHidesTheBanner()
    {
        // A missed session has no status, so "3 of 5 drafts" would be a number with no meaning.
        Assert.Null(Banner(Reported(Week(), Tool("rdp", 3, ("auto_start_off", 1))), HistoryPeriod.Last7Days, status: "draft"));
    }

    [Fact]
    public void NoAnswerOrAnAnswerForAnotherPeriodIsNoBanner()
    {
        Assert.Null(CoverageBanner.Of(null, From(HistoryPeriod.Today), new TableFilter(HistoryPeriod.Today, null, null), Now, Utc));

        // Asked for the week, and the technician has since picked Today: nothing until Today's answer.
        Assert.Null(CoverageBanner.Of(Reported(Week(), Tool("rdp", 3)), From(HistoryPeriod.Last7Days), new TableFilter(HistoryPeriod.Today, null, null), Now, Utc));
    }

    [Fact]
    public void APeriodTheDeviceCouldNotCountAllOfSaysSinceWhen()
    {
        // Last 30 days asked; the device keeps misses for 7, so the counts start a week back and say so.
        var counted = Now.AddDays(-7);
        var words = Banner(Reported(counted, Tool("rdp", 8, ("busy", 2))), HistoryPeriod.Last30Days)!;
        Assert.Equal("Captured 8 of 10 remote sessions since Tue 10 Nov (80%).", words.Headline);
        Assert.Equal("2 not captured: last session still finishing 2.", words.Detail);

        // Any time always says since when; counting that began today says the time.
        Assert.StartsWith("Captured all 3 remote sessions since Tue 10 Nov", Banner(Reported(counted, Tool("rdp", 3)), HistoryPeriod.AnyTime)!.Headline, StringComparison.Ordinal);
        Assert.Equal("Captured all 3 remote sessions since 09:30 today.", Banner(Reported(Now.AddHours(-5.5), Tool("rdp", 3)), HistoryPeriod.Last7Days)!.Headline);
    }

    [Fact]
    public void ThePercentNeverSays100WhileAnyWasMissedNorZeroWhileAnyWasCaptured()
    {
        Assert.Equal(99, new Coverage(199, 200, [(MissReason.Busy, 1)]).Percent);
        Assert.Equal(1, new Coverage(1, 300, [(MissReason.Busy, 299)]).Percent);
        Assert.Equal(100, new Coverage(5, 5, []).Percent);
        Assert.Equal(0, new Coverage(0, 0, []).Percent);
    }

    [Fact]
    public void AReasonThisBuildDoesNotKnowIsStillCountedAsMissed()
    {
        var coverage = SessionHistory.CoverageOf(Reported(Week(), Tool("rdp", 3, ("a_new_reason", 2)), Tool("anydesk", 0, ("busy", 1))));
        Assert.Equal(6, coverage.Total);
        Assert.Equal([(MissReason.Busy, 3)], coverage.Missed);
    }

    [Theory]
    [InlineData(MissReason.AutoStartOff, "auto-start off")]
    [InlineData(MissReason.ToolSwitchedOff, "tool switched off")]
    [InlineData(MissReason.StartHeld, "update installing")]
    [InlineData(MissReason.NoIndicator, "app not running")]
    [InlineData(MissReason.Busy, "last session still finishing")]
    public void EachReasonHasPlainWordsAndAWireName(MissReason reason, string words)
    {
        Assert.Equal(words, CoverageBanner.Words(reason));
        Assert.Equal(reason, MissReasons.Parse(MissReasons.Wire(reason)));
        Assert.DoesNotContain('\u2014', words);
    }

    [Fact]
    public void TheWhenFilterAsksFromTheStartOfItsFirstDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-five-thirty", TimeSpan.FromHours(5.5), "+05:30", "+05:30");
        var from = HistoryFilters.From(HistoryPeriod.Last7Days, Now, zone)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.FromHours(5.5)), from);
        Assert.Null(HistoryFilters.From(HistoryPeriod.AnyTime, Now, zone));
    }

    private static CoverageWords? Banner(CoverageReported reported, HistoryPeriod period, string? tool = null, string? status = null) =>
        CoverageBanner.Of(reported, From(period), new TableFilter(period, status, tool), Now, Utc);

    private static DateTimeOffset? From(HistoryPeriod period) => HistoryFilters.From(period, Now, Utc);

    private static DateTimeOffset Today() => From(HistoryPeriod.Today)!.Value;

    private static DateTimeOffset Week() => From(HistoryPeriod.Last7Days)!.Value;

    private static CoverageReported Reported(DateTimeOffset countedFrom, params ToolCoverage[] tools) =>
        new() { CountedFrom = countedFrom, RetentionDays = 7, Tools = tools };

    private static ToolCoverage Tool(string tool, int captured, params (string Reason, int Count)[] missed) =>
        new() { Tool = tool, Captured = captured, Missed = [.. missed.Select(m => new MissedCount { Reason = m.Reason, Count = m.Count })] };
}
