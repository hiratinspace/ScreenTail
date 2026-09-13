using ScreenTail.Core.Detection;

namespace ScreenTail.Tests.Detection;

/// <summary>ST-022: the rules about what counts as a change, and pulling a tab title out of a window title.</summary>
public sealed class ForegroundDetectionTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstWindowIsReportedImmediately()
    {
        // A session that starts mid-task still needs to know what was already on screen.
        var filter = new ForegroundChangeFilter();

        Assert.NotNull(filter.Offer(Window(1, "mstsc", "RECEPTION-02 — Remote Desktop Connection")));
    }

    [Fact]
    public void TheSameWindowSeenAgainIsNotAChange()
    {
        var filter = new ForegroundChangeFilter();
        var window = Window(1, "mstsc", "RECEPTION-02");
        filter.Offer(window);

        Assert.Null(filter.Offer(window));
        Assert.Null(filter.Flush(window));
    }

    [Fact]
    public void ARetitledWindowIsAChange()
    {
        // A browser moving between tabs never changes window, and it's exactly what ST-023 needs to see.
        var filter = new ForegroundChangeFilter();
        filter.Offer(Window(1, "chrome", "Tickets - Google Chrome"));

        var next = filter.Flush(Window(1, "chrome", "Acme Dental - Google Chrome"));

        Assert.NotNull(next);
        Assert.Equal("Acme Dental", next.BrowserTabTitle);
    }

    [Fact]
    public void WindowsPassedThroughWhileAltTabbingAreNotReported()
    {
        var clock = new ManualTime(At);
        var filter = new ForegroundChangeFilter(clock) { Settle = TimeSpan.FromMilliseconds(60) };
        filter.Offer(Window(1, "mstsc", "RECEPTION-02"));

        // Three windows in 30 ms: the technician is holding alt, not working in any of them.
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Null(filter.Offer(Window(2, "explorer", "Documents")));
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Null(filter.Offer(Window(3, "outlook", "Inbox")));
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Null(filter.Offer(Window(4, "chrome", "Tickets - Google Chrome")));

        // Where they landed does get reported, once things settle.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var landed = filter.Offer(Window(4, "chrome", "Tickets - Google Chrome"));
        Assert.NotNull(landed);
        Assert.Equal("chrome", landed.ProcessName);
    }

    [Fact]
    public void FlushReportsWhateverIsInFrontEvenIfItNeverSettled()
    {
        var clock = new ManualTime(At);
        var filter = new ForegroundChangeFilter(clock) { Settle = TimeSpan.FromSeconds(10) };
        filter.Offer(Window(1, "mstsc", "RECEPTION-02"));

        Assert.Null(filter.Offer(Window(2, "outlook", "Inbox")));
        var flushed = filter.Flush(Window(2, "outlook", "Inbox"));

        Assert.NotNull(flushed);
        Assert.Equal("outlook", flushed.ProcessName);
    }

    [Theory]
    [InlineData("chrome", "Acme Dental — tickets - Google Chrome", "Acme Dental — tickets")]
    [InlineData("chrome", "▶ Training video - Google Chrome", "Training video")]
    [InlineData("msedge", "ConnectWise Manage - Microsoft Edge", "ConnectWise Manage")]
    [InlineData("firefox", "Hudu — Mozilla Firefox", "Hudu")]
    [InlineData("brave", "Ticket #48213 - Brave", "Ticket #48213")]
    public void TheActiveTabComesOutOfTheWindowTitle(string process, string title, string expected) =>
        Assert.Equal(expected, BrowserTitles.ActiveTab(process, title));

    [Fact]
    public void ABrowserWindowWithNoTabReportsNoTab() =>
        Assert.Null(BrowserTitles.ActiveTab("chrome", " - Google Chrome"));

    [Fact]
    public void ABrowserWindowWithoutTheSuffixKeepsItsTitle()
    {
        // Downloads, settings and popups don't carry the browser suffix; the title still describes what's there.
        Assert.Equal("Downloads", BrowserTitles.ActiveTab("chrome", "Downloads"));
    }

    [Theory]
    [InlineData("mstsc", "RECEPTION-02 — Remote Desktop Connection")]
    [InlineData("explorer", "Documents")]
    [InlineData(null, "anything")]
    public void NonBrowsersHaveNoTabTitle(string? process, string title) =>
        Assert.Null(BrowserTitles.ActiveTab(process, title));

    [Fact]
    public void NothingInFrontIsAValidAnswer()
    {
        var none = ForegroundWindowInfo.None(At);

        Assert.True(none.IsNone);
        Assert.Equal("none", none.ForLog());
    }

    [Fact]
    public void TheLogLineCarriesNoWindowTitle()
    {
        // INV-10: a diagnostics line may say how long a title was, never what it said.
        const string Title = "Patient records — Acme Dental - Google Chrome";
        var window = Window(1, "chrome", Title);

        var line = window.ForLog();

        Assert.DoesNotContain("Patient", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", line, StringComparison.Ordinal);
        Assert.Contains("chrome#1", line, StringComparison.Ordinal);
        Assert.Contains($"{Title.Length} chars", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElevatedWindowSaysSoInTheLog()
    {
        var window = Window(1, "mmc", "Services") with { IsElevated = true };

        Assert.Contains("elevated", window.ForLog(), StringComparison.Ordinal);
    }

    private static ForegroundWindowInfo Window(int id, string? process, string title) =>
        new(id, id, process, title, "Window", BrowserTitles.ActiveTab(process, title), false, At);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
