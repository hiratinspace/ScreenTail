using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScreenTail.Core.Store;

namespace ScreenTail.Tests.Store;

/// <summary>
/// The index the session queries actually use (ST-041; 2026-09-20 review).
///
/// <c>frames_pending (redaction_pending, ts_ms)</c> led on a column with two values, one of which is
/// nearly every row in the store, and the planner picked it for every query that filters a single
/// session's frames. It searched the index for "redacted", got the whole store back, and then read each
/// row from the table to find out which session it was in — one decrypted page per row.
///
/// That cost grows with the store rather than with the session, so it arrives with retention: listing
/// sessions took 5.4 seconds over 4,000 frames, with the store's single gate held for all of it and the
/// capture writes queued behind.
///
/// A plan is not a timing, which is the point of testing it this way: it says which index was chosen,
/// on any machine, in a second. The timings that motivated this are in the review notes.
/// </summary>
public sealed class FrameIndexTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"st-index-{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private SqliteSessionStore? _store;

    [Theory]
    // The three that were reading the whole store to answer a question about one session.
    [InlineData("SELECT id, ts_ms FROM frames WHERE session_id = 's1' AND redaction_pending = 0 ORDER BY ts_ms")]
    [InlineData("SELECT COUNT(*) FROM frames WHERE session_id = 's1' AND redaction_pending = 1")]
    [InlineData("SELECT COUNT(*) FROM frames f WHERE f.session_id = 's1' AND f.redaction_pending = 0")]
    public async Task AQuestionAboutOneSessionIsAnsweredFromThatSessionsRows(string sql)
    {
        var plan = await PlanAsync(sql);

        Assert.Contains("frames_by_session_state", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN frames", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRedactionWorkerStillHasAnIndexOfItsOwn()
    {
        // The worker asks for the oldest frame still waiting, across every session. That is what the
        // partial index is for, and it holds only the rows that are actually pending.
        var plan = await PlanAsync("SELECT id, session_id, ts_ms, image FROM frames WHERE redaction_pending = 1 ORDER BY ts_ms LIMIT 1");

        Assert.Contains("frames_pending", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePendingIndexHoldsOnlyPendingFrames()
    {
        // Partial, so it cannot be chosen for "redaction_pending = 0" however the statistics fall, and
        // so it stays a handful of entries rather than one per frame ever captured.
        var sql = await ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'frames_pending'");

        Assert.Contains("WHERE redaction_pending = 1", sql, StringComparison.Ordinal);
    }

    private async Task<string> PlanAsync(string sql)
    {
        var lines = new List<string>();
        await using var connection = await OpenRawAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(3));
        }

        return string.Join("\n", lines);
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = await OpenRawAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// The store's own file, opened directly, because the planner's answer is the thing under test and
    /// the store has no reason to expose EXPLAIN.
    /// </summary>
    private async Task<SqliteConnection> OpenRawAsync()
    {
        _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var key = connection.CreateCommand();
        key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key.GetKey())}'\";";
        await key.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        File.Delete(_path);
    }
}
