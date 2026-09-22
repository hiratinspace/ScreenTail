using ScreenTail.Core.Ipc;

namespace ScreenTail.Tests.Ipc;

/// <summary>
/// What the service knows about whether anything on screen says capture is happening (INV-4; 2026-09-20
/// review).
///
/// It used to know one thing: how many clients were connected to the pipe. A connection is not a pill.
/// The window could be positioned on a monitor that had since been unplugged, the tray icon could be in
/// Windows 11's overflow flyout, and the UI could have frozen while still holding the socket open —
/// and in all three the service went on recording a customer's screen while counting itself indicated.
///
/// <b>What this changes and what it does not.</b> A report is the UI saying "I am showing the pill, and
/// here is where". The service cannot verify that, and a process that lies is a process that could
/// equally well just connect — so this is not a defence against a hostile client, and the comment on the
/// guard says so. What it buys is the accidents: a UI that has stopped painting, stopped running, or is
/// drawing somewhere nobody can see stops reporting, and reports go stale.
/// </summary>
public sealed class IndicatorReportsTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NobodyReportingIsNobodyShowing()
    {
        var reports = new IndicatorReports(new ManualTime(At));

        Assert.False(reports.Showing);
    }

    [Fact]
    public void AWindowThatSaysItIsShowingCounts()
    {
        var reports = new IndicatorReports(new ManualTime(At));

        reports.Report(Guid.NewGuid());

        Assert.True(reports.Showing);
    }

    [Fact]
    public void AReportGoesStale()
    {
        // The frozen UI, which is the case a connection count can never catch: the socket stays open and
        // the pill stops being repainted. Nothing keeps reporting, so nothing keeps counting.
        var clock = new ManualTime(At);
        var reports = new IndicatorReports(clock);
        reports.Report(Guid.NewGuid());

        clock.Advance(IndicatorReports.GoodFor + TimeSpan.FromSeconds(1));

        Assert.False(reports.Showing);
    }

    [Fact]
    public void AWindowThatKeepsSayingSoKeepsCounting()
    {
        var clock = new ManualTime(At);
        var reports = new IndicatorReports(clock);
        var window = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            reports.Report(window);
            clock.Advance(IndicatorReports.GoodFor - TimeSpan.FromSeconds(1));
            Assert.True(reports.Showing);
        }
    }

    [Fact]
    public void OneWindowGoingQuietDoesNotSilenceAnother()
    {
        // Two UIs is unusual and allowed. What matters is that something is showing, not which.
        var clock = new ManualTime(At);
        var reports = new IndicatorReports(clock);
        var quiet = Guid.NewGuid();
        var talkative = Guid.NewGuid();

        reports.Report(quiet);
        clock.Advance(IndicatorReports.GoodFor - TimeSpan.FromSeconds(1));
        reports.Report(talkative);
        clock.Advance(TimeSpan.FromSeconds(2));

        // The first has gone stale and the second has not.
        Assert.True(reports.Showing);
    }

    [Fact]
    public void AWindowThatWentAwayStopsCounting()
    {
        // The pipe noticed the disconnect, which is sooner than the report would have expired.
        var reports = new IndicatorReports(new ManualTime(At));
        var window = Guid.NewGuid();
        reports.Report(window);

        reports.Forget(window);

        Assert.False(reports.Showing);
    }

    [Fact]
    public void ReportsFromWindowsNobodyIsListeningToAreNotKeptForEver()
    {
        // A client that reports once and vanishes without a clean disconnect leaves an entry. It must
        // not accumulate for the life of the service.
        var clock = new ManualTime(At);
        var reports = new IndicatorReports(clock);
        for (var i = 0; i < 100; i++)
        {
            reports.Report(Guid.NewGuid());
            clock.Advance(IndicatorReports.GoodFor + TimeSpan.FromSeconds(1));
        }

        Assert.False(reports.Showing);
        Assert.True(reports.Tracked <= 2, $"{reports.Tracked} stale reports were kept.");
    }
}
