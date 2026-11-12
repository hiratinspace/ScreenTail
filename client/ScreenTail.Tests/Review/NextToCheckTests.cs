using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The receipt's "Next to check" (Spec v0.6.0 §5 S3, §1.1): the next session in the inbox, one key away
/// (Enter, Open next), and the day so far. The inbox groups are To check (drafts waiting) and then Not
/// confirmed (a publish that may have landed), newest first in each.
/// </summary>
public sealed class NextToCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheNewestDraftWaitingIsNext()
    {
        var rows = new[]
        {
            Row("older", "draft", hoursAgo: 5),
            Row("newer", "draft", hoursAgo: 1),
            Row("done", "published", hoursAgo: 0.5),
        };

        Assert.Equal("newer", NextToCheck.Pick(rows, "current")?.Id);
    }

    [Fact]
    public void TheSessionJustPublishedIsNeverNext()
    {
        var rows = new[] { Row("current", "draft", hoursAgo: 0.1), Row("other", "draft", hoursAgo: 3) };

        Assert.Equal("other", NextToCheck.Pick(rows, "current")?.Id);
    }

    [Fact]
    public void ToCheckComesBeforeNotConfirmed()
    {
        var rows = new[]
        {
            Row("unconfirmed", "draft", hoursAgo: 0.2, unconfirmed: true),
            Row("waiting", "draft", hoursAgo: 4),
        };

        Assert.Equal("waiting", NextToCheck.Pick(rows, "current")?.Id);
    }

    [Fact]
    public void NotConfirmedIsNextWhenNothingElseWaits()
    {
        var rows = new[]
        {
            Row("unconfirmed", "partial", hoursAgo: 2, unconfirmed: true),
            Row("done", "published", hoursAgo: 1),
        };

        Assert.Equal("unconfirmed", NextToCheck.Pick(rows, "current")?.Id);
    }

    [Theory]
    [InlineData("published")]
    [InlineData("discarded")]
    [InlineData("pending")]
    [InlineData("partial")]
    public void NothingElseNeedsCheckingWhenNoDraftWaits(string status)
    {
        Assert.Null(NextToCheck.Pick([Row("a", status, hoursAgo: 1)], "current"));
    }

    [Fact]
    public void NoListMeansNoNext()
    {
        Assert.Null(NextToCheck.Pick(null, "current"));
    }

    [Fact]
    public void TheCardNamesTheToolAndHowLongWithNoContent()
    {
        // The row carries a tool, a duration and a start, never a title or a company (INV-10 keeps the
        // list content-free), so the card says what the row can.
        var row = Row("n", "draft", hoursAgo: 2) with { Tool = "rdp", DurationMs = 9 * 60_000 };

        Assert.Equal("Remote Desktop session", NextToCheck.Title(row));
        Assert.StartsWith("9 min · started ", NextToCheck.Meta(row, TimeZoneInfo.Utc), StringComparison.Ordinal);
        Assert.Equal("9 min · started 13:00", NextToCheck.Meta(row, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ANotConfirmedNextSaysSo()
    {
        var row = Row("n", "draft", hoursAgo: 2, unconfirmed: true) with { Tool = "screenconnect", DurationMs = 30_000 };

        Assert.Equal("1 min · started 13:00 · publish not confirmed", NextToCheck.Meta(row, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TodayCountsWhatWasPublishedTodayAndWhatIsLeft()
    {
        var rows = new[]
        {
            Row("current", "published", hoursAgo: 0.2),
            Row("p1", "published", hoursAgo: 3),
            Row("p2", "partial", hoursAgo: 4),
            Row("yesterday", "published", hoursAgo: 30),
            Row("d1", "draft", hoursAgo: 1),
            Row("u1", "draft", hoursAgo: 40, unconfirmed: true),
            Row("gone", "discarded", hoursAgo: 1),
        };

        var today = NextToCheck.Today(rows, "current", Now, TimeZoneInfo.Utc);

        // The session just published counts as published, whatever the list said when it was read.
        Assert.Equal(3, today.Published);
        Assert.Equal(2, today.LeftToCheck);
    }

    private static SessionRow Row(string id, string status, double hoursAgo, bool unconfirmed = false) => new()
    {
        Id = id,
        StartedAt = Now - TimeSpan.FromHours(hoursAgo),
        DurationMs = 60_000,
        Status = status,
        Tool = "screenconnect",
        Frames = 3,
        FramesPurged = 0,
        PublishUnconfirmed = unconfirmed,
    };
}
