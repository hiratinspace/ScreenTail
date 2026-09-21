using System.Diagnostics;
using System.Security.Cryptography;
using ScreenTail.Core.History;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.History;

/// <summary>ST-079: the session history table, its filters, and the number the ticket puts on it.</summary>
public sealed class SessionHistoryTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SqliteSessionStore> _stores = [];

    [Fact]
    public void ADiscardedSessionIsDiscardedWhateverElseItWas()
    {
        // A session somebody threw away is not a draft waiting for them, however complete its note was.
        var discarded = Summary("s1", state: SessionStateNames.Discarded, title: "Printer offline");

        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf(discarded));
    }

    [Fact]
    public void APartialSessionSaysSoRatherThanLookingLikeAnyOtherDraft()
    {
        // The whole point of the badge: a note with missing steps must not sit in the list indistinguishable
        // from a complete one, because the technician is choosing which to publish.
        var partial = Summary("s1", partial: true, title: "Printer offline");

        Assert.Equal(SessionStatus.Partial, SessionHistory.StatusOf(partial));
    }

    [Fact]
    public void ASessionWithNoNoteYetIsPendingAndOneWithANoteIsADraft()
    {
        Assert.Equal(SessionStatus.Pending, SessionHistory.StatusOf(Summary("s1")));
        Assert.Equal(SessionStatus.Draft, SessionHistory.StatusOf(Summary("s2", title: "Printer offline")));
    }

    [Fact]
    public void FilteringByStatusKeepsOnlyThatStatus()
    {
        var sessions = new[]
        {
            Summary("s1", title: "one"),
            Summary("s2", state: SessionStateNames.Discarded),
            Summary("s3", partial: true),
        };

        var drafts = SessionHistory.Filter(sessions, new HistoryFilter { Statuses = new HashSet<SessionStatus> { SessionStatus.Draft } });

        Assert.Equal(["s1"], drafts.Select(s => s.Id));
    }

    [Fact]
    public void TheEndOfADateRangeIncludesThatDaysSessions()
    {
        // "To Friday" means Friday. A range that silently dropped Friday's work would look like the
        // history losing rows, which is the one thing a history must not do.
        var friday = new DateTimeOffset(2026, 9, 11, 16, 30, 0, TimeSpan.Zero);
        var sessions = new[] { Summary("s1", startedAt: friday) };

        var shown = SessionHistory.Filter(sessions, new HistoryFilter { To = friday.AddHours(6) });

        Assert.Single(shown);
    }

    [Fact]
    public void TheCoverageBannerDescribesWhatIsOnScreen()
    {
        // Spec §5 S4 puts it above a filtered table, so it has to describe that table. A banner still
        // reporting the week while the technician is looking at one day answers a question nobody asked -
        // and it is the kind of number that ends up in a QBR.
        var coverage = SessionHistory.CoverageOf([Summary("s1"), Summary("s2")], observed: 4);

        Assert.Equal("Captured 2 of 4 remote sessions (50%)", coverage.Banner);
    }

    [Fact]
    public void AnEmptyHistoryHasNoBannerRatherThanZeroOfZero()
    {
        Assert.Null(SessionHistory.CoverageOf([]).Banner);
    }

    [Fact]
    public void CoverageNeverClaimsMoreSessionsThanWereCaptured()
    {
        // The observed count comes from detection (ST-023) and can lag behind what was actually stored.
        // "Captured 5 of 3" would be read as a bug in the product rather than in the counter.
        var coverage = SessionHistory.CoverageOf([Summary("s1"), Summary("s2"), Summary("s3")], observed: 1);

        Assert.Equal(3, coverage.Total);
        Assert.Equal(100, coverage.Percent);
    }

    [Fact]
    public void BulkDiscardCountsOnlyWhatItWillActuallyDestroy()
    {
        // v0.4.1 Q6 confirms with the number selected. Asking a technician to type "3" when only two
        // sessions will change has misled them about the thing the gate exists to slow down.
        var selected = new[]
        {
            Summary("s1", title: "one"),
            Summary("s2", state: SessionStateNames.Discarded),
            Summary("s3", title: "three"),
        };

        Assert.Equal(["s1", "s3"], SessionHistory.Discardable(selected).Select(s => s.Id));
    }

    [Fact]
    public async Task TwoHundredSessionsListInWellUnderHalfASecond()
    {
        // The ticket's number, against the real encrypted store. The shape that fails it is the obvious
        // one: list the ids, then LoadSessionAsync each - which reads every frame, event and transcript
        // segment of a session to build one row. Two hundred of those is the whole store.
        var store = await OpenAsync();
        for (var i = 0; i < 200; i++)
        {
            var id = $"s{i:D3}";
            await store.CreateSessionAsync(new NewSession(
                id,
                DateTimeOffset.UnixEpoch.AddMinutes(i),
                new RemoteTool { Kind = RemoteToolKind.Screenconnect },
                LocalOnly: false,
                PolicyVersion: "v14"));
            await store.StageFrameAsync(id, new StagedFrame($"f{i}", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 0xAA }));
            await store.MarkFrameRedactedAsync($"f{i}", new RedactionOutcome(new byte[] { 0xBB }, null, [], SensitiveContext: false, DateTimeOffset.UnixEpoch));
        }

        var clock = Stopwatch.StartNew();
        var rows = await store.ListSessionsAsync();
        clock.Stop();

        Assert.Equal(200, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.Frames));
        Assert.True(clock.ElapsedMilliseconds < 500, $"listing 200 sessions took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheListReadsOnlyAsManyRowsAsItWasAskedFor()
    {
        // 2026-09-20 efficiency review. The query had no LIMIT and the caller applied .Take() after every
        // row had been read, parsed and turned into a summary -- including a JSON deserialise per row.
        // With retention at a week that is every session the machine has ever recorded, to show ten.
        var store = await OpenAsync();
        for (var i = 0; i < 40; i++)
        {
            await store.CreateSessionAsync(new NewSession($"s{i:D2}", DateTimeOffset.UnixEpoch.AddMinutes(i), Rdp, false, null));
        }

        var rows = await store.ListSessionsAsync(limit: 5);

        Assert.Equal(5, rows.Count);
        Assert.Equal("s39", rows[0].Id);
    }

    [Fact]
    public async Task ATitleSurvivesADraftWhoseOtherFieldsAreNonsense()
    {
        // The title was read by deserialising the whole note into DraftNote, so one field of the wrong
        // shape threw and the row lost its title -- a history that says "Draft" where the technician
        // wrote a name. Reading the one value out of the JSON in SQL cannot be thrown off by the rest,
        // and does not pull every step's text out of the database to show one line.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, Rdp, false, null));
        await store.SetSessionStateAsync("s1", "draft_ready", null);
        await SetDraftJsonAsync(store, "s1", """{"suggested_title":"Printer offline","steps":"not-an-array"}""");

        var reopened = await OpenAsync();
        var row = Assert.Single(await reopened.ListSessionsAsync());

        Assert.Equal("Printer offline", row.Title);
    }

    [Fact]
    public async Task TheListCarriesNoContentBeyondTheTechniciansOwnTitle()
    {
        // INV-10, and this list is rendered beside a customer. The only free text is the note's suggested
        // title, which the technician wrote; nothing here can hold OCR, transcript or a window title.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, "v14"));
        await store.StageFrameAsync("s1", new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 0xAA }));
        await store.MarkFrameRedactedAsync("f1", new RedactionOutcome(new byte[] { 0xBB }, "SECRET OCR TEXT", [], SensitiveContext: false, DateTimeOffset.UnixEpoch));
        await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = "t1", TsMs = 1, EndMs = 2, Speaker = Speaker.Tech, Text = "SECRET SPOKEN WORDS" });

        var row = Assert.Single(await store.ListSessionsAsync());

        var everything = string.Join("|", row.Id, row.RemoteTool, row.State, row.Title ?? string.Empty);
        Assert.DoesNotContain("SECRET", everything, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };

    /// <summary>
    /// Writes the draft column directly, because SaveDraftAsync takes a DraftNote and the shape under
    /// test is one DraftNote cannot hold. The store is closed first: its connection is the only writer.
    /// </summary>
    private async Task SetDraftJsonAsync(SqliteSessionStore store, string sessionId, string json)
    {
        await store.DisposeAsync();
        _ = _stores.Remove(store);

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var key = connection.CreateCommand())
            {
                key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key.GetKey())}'\";";
                _ = await key.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var write = connection.CreateCommand();
            write.CommandText = "UPDATE sessions SET draft_json = @json WHERE id = @id";
            _ = write.Parameters.AddWithValue("@json", json);
            _ = write.Parameters.AddWithValue("@id", sessionId);
            _ = await write.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static SessionSummary Summary(
        string id,
        DateTimeOffset? startedAt = null,
        string state = "draft_ready",
        bool partial = false,
        string? title = null,
        string tool = "screenconnect") =>
        new(id, startedAt ?? DateTimeOffset.UnixEpoch, 60_000, tool, state, partial, 0, 0, false, title);

    private async Task<SqliteSessionStore> OpenAsync()
    {
        var store = await SqliteSessionStore.OpenAsync(_path, _key);
        _stores.Add(store);
        return store;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
