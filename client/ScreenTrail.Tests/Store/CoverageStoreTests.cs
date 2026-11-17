using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScreenTrail.Core.History;
using ScreenTrail.Core.Sessions;
using ScreenTrail.Core.Store;
using ScreenTrail.Shared.Ipc;
using ScreenTrail.Shared.Schema;
using ScreenTrail.Tests.Sessions;

namespace ScreenTrail.Tests.Store;

/// <summary>
/// ST-079's denominator in the store (migration 0016, spec v0.7.3 S4): what a missed remote session keeps
/// (INV-10), how long (INV-12), how the counts are taken, and how a brief pass-through is left out of both.
/// </summary>
public sealed class CoverageStoreTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentrail-tests", $"{Guid.NewGuid():N}.db");
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ManualTime _time = new(T0);
    private readonly List<SqliteSessionStore> _stores = [];

    [Fact]
    public async Task AMissedVisitKeepsItsToolItsTimesAndAReasonWordAndNothingElse()
    {
        var store = await OpenAsync();
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Anydesk, T0, T0.AddMinutes(12), MissReason.AutoStartOff), Ct);

        await using var raw = await OpenRawAsync();
        await using var columns = raw.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('missed_visits') ORDER BY cid";
        var names = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        // No title, no process, no ticket: there is nowhere to put one (INV-10).
        Assert.Equal(["id", "remote_tool_kind", "started_at", "ended_at", "reason"], names);
        await using var row = raw.CreateCommand();
        row.CommandText = "SELECT remote_tool_kind || '|' || reason FROM missed_visits";
        Assert.Equal("anydesk|auto_start_off", (string?)await row.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task TheStoreRefusesAReasonThatIsNotOneOfItsWords()
    {
        _ = await OpenAsync();
        await using var raw = await OpenRawAsync();
        await using var insert = raw.CreateCommand();
        insert.CommandText = "INSERT INTO missed_visits (remote_tool_kind, started_at, ended_at, reason) VALUES ('anydesk', '2026-11-10T09:00:00.0000000+00:00', '2026-11-10T09:05:00.0000000+00:00', 'Inbox - Outlook')";
        var refused = await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Contains("CHECK", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCountsStartFromTheMomentAskedForWhateverOffsetASessionWasStartedIn()
    {
        var store = await OpenAsync();

        // 08:00 in New York is 13:00 UTC, after the 12:00 UTC start; 06:00 there is 11:00 UTC, before it.
        var newYork = TimeSpan.FromHours(-5);
        await store.CreateSessionAsync(Session("after", new DateTimeOffset(2026, 11, 10, 8, 0, 0, newYork), RemoteToolKind.Screenconnect));
        await store.CreateSessionAsync(Session("before", new DateTimeOffset(2026, 11, 10, 6, 0, 0, newYork), RemoteToolKind.Screenconnect));
        await store.CreateSessionAsync(Session("rdp", T0.AddHours(5), RemoteToolKind.Rdp));
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Screenconnect, T0.AddHours(4), T0.AddHours(4.5), MissReason.StartHeld), Ct);
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Screenconnect, T0.AddHours(5), T0.AddHours(5.5), MissReason.StartHeld), Ct);
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Screenconnect, T0.AddHours(6), T0.AddHours(6.5), MissReason.AutoStartOff), Ct);
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Anydesk, T0.AddHours(1), T0.AddHours(2), MissReason.Busy), Ct);

        var counts = await store.CountCoverageAsync(T0.AddHours(3), Ct);

        Assert.Equal(1, counts.Captured["screenconnect"]);
        Assert.Equal(1, counts.Captured["rdp"]);
        Assert.Equal(2, counts.Missed[("screenconnect", MissReason.StartHeld)]);
        Assert.Equal(1, counts.Missed[("screenconnect", MissReason.AutoStartOff)]);
        Assert.False(counts.Missed.ContainsKey(("anydesk", MissReason.Busy)));
    }

    [Fact]
    public async Task APassThroughIsLeftOutOfTheCapturedCountAndStaysADiscardedRow()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("kept", T0, RemoteToolKind.Anydesk));
        await store.CreateSessionAsync(Session("brief", T0.AddMinutes(30), RemoteToolKind.Anydesk));
        _ = await store.DiscardSessionAsync("brief", Ct);

        Assert.True(await store.MarkPassThroughAsync("brief", Ct));
        Assert.True(await store.MarkPassThroughAsync("brief", Ct));
        Assert.False(await store.MarkPassThroughAsync("no-such-session", Ct));

        Assert.Equal(1, (await store.CountCoverageAsync(T0, Ct)).Captured["anydesk"]);
        Assert.Equal(2, (await store.ListSessionsAsync(ct: Ct)).Count);

        // A session gone altogether takes its mark with it.
        await store.DeleteSessionAsync("brief", Ct);
        await using var raw = await OpenRawAsync();
        await using var marks = raw.CreateCommand();
        marks.CommandText = "SELECT COUNT(*) FROM pass_through_sessions";
        Assert.Equal(0L, (long)(await marks.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task MissedVisitsAgeOutWithTheRawData()
    {
        // INV-12: the retention purge takes the misses that ended before its cutoff, and only those.
        var store = await OpenAsync();
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Rdp, T0, T0.AddHours(1), MissReason.AutoStartOff), Ct);
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Rdp, T0.AddDays(2), T0.AddDays(2).AddHours(1), MissReason.AutoStartOff), Ct);
        var job = new RetentionJob(store, _time, new RetentionOptions());

        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromHours(2));
        _ = await job.RunAsync(Ct);

        var counts = await store.CountCoverageAsync(T0.AddYears(-1), Ct);
        Assert.Equal(1, counts.Missed[("rdp", MissReason.AutoStartOff)]);
    }

    [Fact]
    public async Task CountingBeganWhenTheMigrationWasApplied()
    {
        var store = await OpenAsync();
        Assert.Equal(T0, await store.CoverageCountedSinceAsync(Ct));
    }

    [Fact]
    public async Task AStoreFromBeforeTheCoverageMigrationUpgradesAndBeginsCountingThen()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("old", T0, RemoteToolKind.Rdp));
        await store.DisposeAsync();
        _stores.Remove(store);

        await using (var raw = await OpenRawAsync())
        {
            await using var down = raw.CreateCommand();
            down.CommandText = "DROP TABLE missed_visits; DROP TABLE pass_through_sessions; DELETE FROM schema_migrations WHERE version >= 16;";
            await down.ExecuteNonQueryAsync(Ct);
        }

        _time.Advance(TimeSpan.FromDays(3));
        var upgraded = await OpenAsync();

        Assert.Equal(SqliteSessionStore.LatestSchemaVersion, await upgraded.GetSchemaVersionAsync(Ct));
        Assert.Equal(T0.AddDays(3), await upgraded.CoverageCountedSinceAsync(Ct));
        Assert.Equal(1, (await upgraded.CountCoverageAsync(T0, Ct)).Captured["rdp"]);
        await upgraded.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Rdp, T0.AddDays(3), T0.AddDays(3).AddHours(1), MissReason.Busy), Ct);
        Assert.Equal(1, (await upgraded.CountCoverageAsync(T0, Ct)).Missed[("rdp", MissReason.Busy)]);
    }

    [Fact]
    public async Task TheServiceCountsFromNoEarlierThanItCanCountHonestly()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("early", T0.AddHours(-1), RemoteToolKind.Rdp));
        await store.CreateSessionAsync(Session("a", T0.AddDays(1), RemoteToolKind.Rdp));
        await store.CreateSessionAsync(Session("b", T0.AddDays(9), RemoteToolKind.Rdp));
        await store.RecordMissedVisitAsync(new MissedVisit(RemoteToolKind.Anydesk, T0.AddDays(9), T0.AddDays(9).AddHours(1), MissReason.ToolSwitchedOff), Ct);
        var commands = new CoverageCommands(store, () => TimeSpan.FromDays(7), _time);

        // Any time, before a week has passed: from when counting began, so the session before it is out.
        _time.Advance(TimeSpan.FromDays(2));
        var first = await commands.ReplyAsync(new GetCoverageCommand { RequestId = 7 }, Ct);
        Assert.Equal(7, first.RequestId);
        Assert.Equal(T0, first.CountedFrom);
        Assert.Equal(7, first.RetentionDays);
        Assert.Equal(2, Assert.Single(first.Tools, t => t.Tool == "rdp").Captured);

        // Ten days on, Last 30 days: from the retention's cutoff, since misses before it are gone.
        _time.Advance(TimeSpan.FromDays(8));
        var month = await commands.ReplyAsync(new GetCoverageCommand { RequestId = 8, From = T0.AddDays(-20) }, Ct);
        Assert.Equal(T0.AddDays(3), month.CountedFrom);
        Assert.Equal(1, Assert.Single(month.Tools, t => t.Tool == "rdp").Captured);
        var anydesk = Assert.Single(month.Tools, t => t.Tool == "anydesk");
        Assert.Equal(0, anydesk.Captured);
        Assert.Equal("tool_switched_off", Assert.Single(anydesk.Missed).Reason);

        // Today: the period's own start, which is later than both.
        var today = await commands.ReplyAsync(new GetCoverageCommand { RequestId = 9, From = T0.AddDays(10).AddHours(-1) }, Ct);
        Assert.Equal(T0.AddDays(10).AddHours(-1), today.CountedFrom);
        Assert.Empty(today.Tools);
    }

    [Fact]
    public async Task AnEmptySessionTheMachineDropsIsAPassThroughInNeitherNumber()
    {
        // #338's discard of a brief, empty session: the row stays discarded, and it is not counted captured.
        await using var harness = await MachineHarness.StartAsync(emptySessionMinimum: EmptySession.Minimum);
        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Anydesk }, ct: Ct));
        Assert.True(await harness.Machine.StopAsync(Ct));

        var row = Assert.Single(await harness.Store.ListSessionsAsync(ct: Ct));
        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf(row));
        Assert.Empty((await harness.Store.CountCoverageAsync(DateTimeOffset.UnixEpoch, Ct)).Captured);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NewSession Session(string id, DateTimeOffset started, RemoteToolKind tool) =>
        new(id, started, new RemoteTool { Kind = tool }, LocalOnly: false, PolicyVersion: null);

    private async Task<SqliteSessionStore> OpenAsync()
    {
        var store = await SqliteSessionStore.OpenAsync(_path, new Key(_key), _time);
        _stores.Add(store);
        return store;
    }

    private async Task<SqliteConnection> OpenRawAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(Ct);
        StoreKey.Apply(connection, (byte[])_key.Clone());
        return connection;
    }

    private sealed class Key(byte[] key) : IStoreKeyProvider
    {
        public int KeyLength => 32;

        public byte[] GetKey() => (byte[])key.Clone();
    }
}
