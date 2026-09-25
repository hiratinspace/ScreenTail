using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Outbox;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Store;

/// <summary>
/// <see cref="ISessionStore"/> on SQLite + SQLCipher. One connection, commands serialized, WAL journal.
/// Open with <see cref="OpenAsync"/>; it keys the connection and applies pending migrations.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IAuditLog, IOutboxStore
{
    private static readonly Lock BatteriesLock = new();
    private static readonly Lazy<int> LatestVersion = new(() => LoadMigrations().Max(m => m.Version));
    private static bool _batteriesReady;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private bool _disposed;

    private SqliteSessionStore(SqliteConnection connection, TimeProvider time)
    {
        _connection = connection;
        _time = time;
    }

    /// <summary>Highest embedded migration number; what a freshly opened store reports from <see cref="GetSchemaVersionAsync"/>.</summary>
    public static int LatestSchemaVersion => LatestVersion.Value;

    /// <param name="time">Clock for created/updated/ended timestamps; tests inject one to age sessions (ST-044).</param>
    public static async Task<SqliteSessionStore> OpenAsync(string path, IStoreKeyProvider keys, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keys);
        time ??= TimeProvider.System;
        EnsureBatteries();

        var key = keys.GetKey();
        if (key.Length != keys.KeyLength)
        {
            throw new StoreKeyException($"The key provider returned {key.Length} bytes; {keys.KeyLength} are required.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Pooling off: a pooled connection handed back without PRAGMA key would see ciphertext.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await KeyAsync(connection, key, ct).ConfigureAwait(false);
            // secure_delete: a deleted row's pages are overwritten, not left on the freelist with their
            // contents intact until something reuses them. SQLCipher builds with it on; it is said here
            // so that a provider swap or a stray pragma cannot quietly take it away, because INV-12's
            // promise is that the data is gone, not that it is hard to read for whoever holds the key
            // later (P2-12). The retention pass pays for it, not the capture path.
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA secure_delete = ON;", ct).ConfigureAwait(false);
            await MigrateAsync(connection, time, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            Array.Clear(key);
        }

        return new SqliteSessionStore(connection, time);
    }

    public Task CreateSessionAsync(NewSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = Iso(_time.GetUtcNow());
        return RunAsync(
            """
            INSERT INTO sessions (id, started_at, remote_tool_kind, remote_tool_version, local_only, policy_version, suggested_ticket, created_at, updated_at)
            VALUES (@id, @started, @kind, @version, @local, @policy, @ticket, @now, @now)
            """,
            ct,
            ("@id", session.SessionId),
            ("@started", Iso(session.StartedAt)),
            ("@kind", EnumName(session.RemoteTool.Kind)),
            ("@version", session.RemoteTool.ClientVersion),
            ("@local", session.LocalOnly ? 1 : 0),
            ("@policy", session.PolicyVersion),
            ("@ticket", session.SuggestedTicket),
            ("@now", now));
    }

    public Task AppendEventAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // Serialized as the base type so the "type" discriminator is written.
        var json = JsonSerializer.Serialize<SessionEvent>(sessionEvent, SessionJson.Options);
        return RunAsync(
            """
            INSERT INTO events (session_id, seq, ts_ms, json)
            VALUES (@session, (SELECT COALESCE(MAX(seq), -1) + 1 FROM events WHERE session_id = @session), @ts, @json)
            """,
            ct,
            ("@session", sessionId),
            ("@ts", sessionEvent.TsMs),
            ("@json", json));
    }

    public Task AppendTranscriptAsync(string sessionId, TranscriptSegment segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return RunAsync(
            """
            INSERT INTO transcript (id, session_id, ts_ms, end_ms, speaker, text, frame_id, confidence)
            VALUES (@id, @session, @ts, @end, @speaker, @text, @frame, @confidence)
            """,
            ct,
            ("@id", segment.Id),
            ("@session", sessionId),
            ("@ts", segment.TsMs),
            ("@end", segment.EndMs),
            ("@speaker", EnumName(segment.Speaker)),
            ("@text", segment.Text),
            ("@frame", segment.FrameId),
            ("@confidence", segment.Confidence));
    }

    public Task StageFrameAsync(string sessionId, StagedFrame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return RunAsync(
            """
            INSERT INTO frames (id, session_id, ts_ms, trigger, width, height, cursor_x, cursor_y, redaction_pending, image)
            VALUES (@id, @session, @ts, @trigger, @w, @h, @cx, @cy, 1, @image)
            """,
            ct,
            ("@id", frame.Id),
            ("@session", sessionId),
            ("@ts", frame.TsMs),
            ("@trigger", EnumName(frame.Trigger)),
            ("@w", frame.Width),
            ("@h", frame.Height),
            ("@cx", frame.Cursor?.X),
            ("@cy", frame.Cursor?.Y),
            ("@image", frame.Image.ToArray()));
    }

    /// <summary>
    /// Writes a frame that has already been read and masked (ADR-0006).
    ///
    /// The only insert the running service makes. A frame used to arrive unredacted and be updated in
    /// place once the worker had read it, which is why <c>redaction_pending</c> was ever a state on
    /// disk; it waits in memory now, so a row written here was never pending and no window could have
    /// been open on it.
    ///
    /// <paramref name="frame"/> is what was captured and carries the row's shape — when, how big, what
    /// triggered it. Its image is deliberately not written: <paramref name="outcome"/> holds the only
    /// picture allowed on disk.
    /// </summary>
    public Task SaveRedactedFrameAsync(
        string sessionId,
        StagedFrame frame,
        RedactionOutcome outcome,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(outcome);

        return RunAsync(
            """
            INSERT INTO frames (id, session_id, ts_ms, trigger, width, height, cursor_x, cursor_y,
                                redaction_pending, image, ocr_text, masked_regions_json, sensitive_context, redacted_at)
            VALUES (@id, @session, @ts, @trigger, @w, @h, @cx, @cy,
                    0, @image, @ocr, @regions, @sensitive, @at)
            """,
            ct,
            ("@id", frame.Id),
            ("@session", sessionId),
            ("@ts", frame.TsMs),
            ("@trigger", EnumName(frame.Trigger)),
            ("@w", frame.Width),
            ("@h", frame.Height),
            ("@cx", frame.Cursor?.X),
            ("@cy", frame.Cursor?.Y),
            ("@image", outcome.RedactedImage.ToArray()),
            ("@ocr", outcome.OcrText),
            ("@regions", JsonSerializer.Serialize(outcome.MaskedRegions, SessionJson.Options)),
            ("@sensitive", outcome.SensitiveContext ? 1 : 0),
            ("@at", Iso(outcome.RedactedAt)));
    }

    public Task<PendingFrame?> TakeNextPendingFrameAsync(CancellationToken ct = default) =>
        TakeNextPendingFrameAsync(null, ct);

    /// <param name="except">
    /// Frames another worker is already redacting. Without this, two workers select the same row — the
    /// query cannot claim it, because a frame stays <c>redaction_pending</c> until the redaction finishes
    /// — and the slower one's MarkFrameRedactedAsync then finds nothing to update and throws.
    /// </param>
    public Task<PendingFrame?> TakeNextPendingFrameAsync(IReadOnlySet<string>? except, CancellationToken ct = default)
    {
        // Built rather than parameterised because the set is a handful of ids the caller owns, never user
        // input; SQLite has no array parameter, and the ids are our own GUID-derived strings.
        var skip = except is { Count: > 0 }
            ? " AND id NOT IN (" + string.Join(",", except.Select((_, i) => "@skip" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + ")"
            : string.Empty;
        var parameters = except is { Count: > 0 }
            ? except.Select((id, i) => ("@skip" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), (object?)id)).ToArray()
            : [];

        return QueryAsync(
            "SELECT id, session_id, ts_ms, image FROM frames WHERE redaction_pending = 1" + skip + " ORDER BY ts_ms LIMIT 1",
            async reader => await reader.ReadAsync(ct).ConfigureAwait(false)
                ? new PendingFrame(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), (byte[])reader[3])
                : null,
            ct,
            parameters);
    }

    public async Task MarkFrameRedactedAsync(string frameId, RedactionOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var changed = await RunAsync(
            """
            UPDATE frames
            SET image = @image, ocr_text = @ocr, masked_regions_json = @regions, sensitive_context = @sensitive,
                redacted_at = @at, redaction_pending = 0
            WHERE id = @id AND redaction_pending = 1
            """,
            ct,
            ("@id", frameId),
            ("@image", outcome.RedactedImage.ToArray()),
            ("@ocr", outcome.OcrText),
            ("@regions", JsonSerializer.Serialize(outcome.MaskedRegions, SessionJson.Options)),
            ("@sensitive", outcome.SensitiveContext ? 1 : 0),
            ("@at", Iso(outcome.RedactedAt))).ConfigureAwait(false);

        if (changed != 1)
        {
            throw new InvalidOperationException($"Frame '{frameId}' is not pending redaction (missing or already redacted).");
        }
    }

    public Task<int> CountPendingFramesAsync(string sessionId, CancellationToken ct = default) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM frames WHERE session_id = @session AND redaction_pending = 1", ct, ("@session", sessionId));

    public Task<int> CountAllPendingFramesAsync(CancellationToken ct = default) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM frames WHERE redaction_pending = 1", ct);

    /// <summary>
    /// Counts a frame that was captured and never stored (ADR-0006, ADR-0004).
    ///
    /// There is no row to delete: the frame waited in memory and the worker decided it could not be
    /// stored, so nothing was ever written. What still has to happen is the counting, because
    /// <c>frames_purged_unredacted</c> reaches the bundle and is what lets the draft say the session has
    /// a hole in it rather than claim to have seen all of it.
    ///
    /// The counter and the audit row together, in one transaction, for the same reason the two halves of
    /// an audit append are: a count without its row, or a row without its count, is a session that
    /// disagrees with itself.
    /// </summary>
    public async Task RecordPurgedFramesAsync(string sessionId, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (count <= 0)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            _ = await ExecuteAsync(
                _connection,
                "UPDATE sessions SET frames_purged_unredacted = frames_purged_unredacted + @count, updated_at = @now WHERE id = @session",
                ct,
                ("@count", count),
                ("@now", Iso(_time.GetUtcNow())),
                ("@session", sessionId)).ConfigureAwait(false);
            _ = await AuditAsync(sessionId, AuditTypes.FramesPurgedUnredacted, count, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DiscardPendingFrameAsync(string frameId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Read against the connection directly rather than through QueryAsync: the gate is already held
            // here, and QueryAsync takes it too, which deadlocks rather than failing.
            //
            // The session is read before the delete, because afterwards the row is gone and there is
            // nothing left to count the loss against.
            string? sessionId;
            await using (var lookup = Command(_connection, "SELECT session_id FROM frames WHERE id = @id AND redaction_pending = 1", ("@id", frameId)))
            await using (var reader = await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                sessionId = await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetString(0) : null;
            }

            if (sessionId is null)
            {
                return;
            }

            await ExecuteAsync(_connection, "DELETE FROM frames WHERE id = @id", ct, ("@id", frameId)).ConfigureAwait(false);
            await ExecuteAsync(
                _connection,
                "UPDATE sessions SET frames_purged_unredacted = frames_purged_unredacted + 1, updated_at = @now WHERE id = @session",
                ct,
                ("@now", Iso(_time.GetUtcNow())),
                ("@session", sessionId)).ConfigureAwait(false);
            await AuditAsync(sessionId, AuditTypes.FramesPurgedUnredacted, 1, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PurgePendingFramesAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var purged = await ExecuteAsync(
                _connection,
                "DELETE FROM frames WHERE session_id = @session AND redaction_pending = 1",
                ct,
                ("@session", sessionId)).ConfigureAwait(false);
            if (purged > 0)
            {
                await ExecuteAsync(
                    _connection,
                    "UPDATE sessions SET frames_purged_unredacted = frames_purged_unredacted + @n, updated_at = @now WHERE id = @session",
                    ct,
                    ("@n", purged),
                    ("@now", Iso(_time.GetUtcNow())),
                    ("@session", sessionId)).ConfigureAwait(false);
                await AuditAsync(sessionId, AuditTypes.FramesPurgedUnredacted, purged, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return purged;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetFrameExcludedAsync(string frameId, bool excluded, CancellationToken ct = default) =>
        RunAsync("UPDATE frames SET excluded_by_user = @x WHERE id = @id", ct, ("@id", frameId), ("@x", excluded ? 1 : 0));

    public async Task<bool> DeleteFrameAsync(string frameId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // The session id is read before the delete, because after it there is nothing left to read it
            // from and an audit row with no session cannot be found again by the person it concerns.
            var sessionId = await QueryInTransactionAsync(
                "SELECT session_id FROM frames WHERE id = @id",
                async reader => await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetString(0) : null,
                ct,
                ("@id", frameId)).ConfigureAwait(false);

            if (sessionId is null)
            {
                return false;
            }

            await ExecuteAsync(_connection, "DELETE FROM frames WHERE id = @id", ct, ("@id", frameId)).ConfigureAwait(false);
            await AuditAsync(sessionId, AuditTypes.FrameDeletedByUser, 1, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyUserBlurAsync(string frameId, ReadOnlyMemory<byte> image, MaskedRegion region, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var existing = await QueryInTransactionAsync(
                "SELECT masked_regions_json FROM frames WHERE id = @id AND redaction_pending = 0",
                async reader => await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetString(0) : null,
                ct,
                ("@id", frameId)).ConfigureAwait(false);

            if (existing is null)
            {
                // A frame still pending redaction has no readable image to blur, and one that is gone
                // cannot be written to. Either way the caller is working from a stale strip.
                throw new InvalidOperationException($"Frame {frameId} is not a redacted frame.");
            }

            var regions = JsonSerializer.Deserialize<List<MaskedRegion>>(existing, SessionJson.Options) ?? [];
            regions.Add(region);

            // The blurred bytes replace the original in the same statement that records the region. There
            // is no moment where the frame is on disk unblurred but claiming to be blurred, and none where
            // it claims a region it does not have.
            await ExecuteAsync(
                _connection,
                "UPDATE frames SET image = @image, masked_regions_json = @regions WHERE id = @id",
                ct,
                ("@id", frameId),
                ("@image", image.ToArray()),
                ("@regions", JsonSerializer.Serialize(regions, SessionJson.Options))).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveDraftAsync(string sessionId, DraftNote draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return RunAsync(
            "UPDATE sessions SET draft_json = @draft, updated_at = @now WHERE id = @id",
            ct,
            ("@draft", JsonSerializer.Serialize(draft, SessionJson.Options)),
            ("@now", Iso(_time.GetUtcNow())),
            ("@id", sessionId));
    }

    public Task FinalizeSessionAsync(string sessionId, FinalizeInfo info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        return RunAsync(
            "UPDATE sessions SET duration_ms = @duration, partial_capture = @partial, ended_at = @now, updated_at = @now WHERE id = @id",
            ct,
            ("@duration", info.DurationMs),
            ("@partial", info.PartialCapture ? 1 : 0),
            ("@now", Iso(_time.GetUtcNow())),
            ("@id", sessionId));
    }

    public Task SetSessionStateAsync(string sessionId, string state, string? reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var now = Iso(_time.GetUtcNow());
        return RunAsync(
            "UPDATE sessions SET state = @state, state_reason = @reason, state_changed_at = @now, updated_at = @now WHERE id = @id",
            ct,
            ("@state", state),
            ("@reason", reason),
            ("@now", now),
            ("@id", sessionId));
    }

    public Task<IReadOnlyList<string>> ListSessionsInStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        // One parameter per state; the list is small and comes from code, never from input.
        var parameters = states.Select((state, i) => ($"@s{i}", (object?)state)).ToArray();
        var sql = $"SELECT id FROM sessions WHERE state IN ({string.Join(", ", parameters.Select(p => p.Item1))}) ORDER BY started_at, id";
        return QueryAsync<IReadOnlyList<string>>(
            sql,
            async reader =>
            {
                var ids = new List<string>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }

                return ids;
            },
            ct,
            parameters);
    }

    public Task<long> GetLastTimestampAsync(string sessionId, CancellationToken ct = default) =>
        ScalarAsync<long>(
            """
            SELECT MAX(ts) FROM (
                SELECT COALESCE(MAX(ts_ms), 0) AS ts FROM events WHERE session_id = @id
                UNION ALL SELECT COALESCE(MAX(ts_ms), 0) FROM frames WHERE session_id = @id
                UNION ALL SELECT COALESCE(MAX(end_ms), 0) FROM transcript WHERE session_id = @id)
            """,
            ct,
            ("@id", sessionId));

    public Task<IReadOnlyList<string>> ListSessionsWithRawDataOlderThanAsync(DateTimeOffset cutoff, string? activeSessionId = null, CancellationToken ct = default) =>
        QueryAsync<IReadOnlyList<string>>(
            // COALESCE, not "ended_at IS NOT NULL": a session that never reached finalize (a crash whose
            // recovery never ran) must still age out, or its raw frames would outlive retention (INV-12).
            // The one exemption is the session being recorded right now; "IS NOT" keeps it NULL-safe.
            """
            SELECT id FROM sessions
            WHERE raw_purged_at IS NULL
              AND COALESCE(ended_at, created_at) < @cutoff
              AND id IS NOT @active
            ORDER BY COALESCE(ended_at, created_at), id
            """,
            async reader =>
            {
                var ids = new List<string>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }

                return ids;
            },
            ct,
            ("@cutoff", Iso(cutoff.ToUniversalTime())),
            ("@active", activeSessionId));

    public async Task<int> PurgeRawDataAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var frames = await ExecuteAsync(_connection, "DELETE FROM frames WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            await ExecuteAsync(_connection, "DELETE FROM transcript WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            await ExecuteAsync(_connection, "DELETE FROM events WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);

            // ST-064 AC3. A queued publish holds the note it was going to send, so leaving it would keep
            // the session's text past the tenant's retention window in the one table nobody thinks to look
            // at (INV-12). Work that has not gone by now is not going. The session row survives a purge,
            // so the foreign key never cascades and this has to be explicit.
            await ExecuteAsync(_connection, "DELETE FROM outbox WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            var now = Iso(_time.GetUtcNow());
            await ExecuteAsync(
                _connection,
                "UPDATE sessions SET raw_purged_at = @now, updated_at = @now WHERE id = @id",
                ct,
                ("@now", now),
                ("@id", sessionId)).ConfigureAwait(false);
            await AuditAsync(sessionId, AuditTypes.RetentionPurged, frames, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return frames;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> DiscardSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var frames = await ExecuteAsync(_connection, "DELETE FROM frames WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            await ExecuteAsync(_connection, "DELETE FROM transcript WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            await ExecuteAsync(_connection, "DELETE FROM events WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);

            // The technician asked for this session to go. Queued work for it goes with it, or a
            // discarded session publishes itself half an hour later (ST-064).
            await ExecuteAsync(_connection, "DELETE FROM outbox WHERE session_id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            var now = Iso(_time.GetUtcNow());
            var discarded = await ExecuteAsync(
                _connection,
                "UPDATE sessions SET draft_json = NULL, raw_purged_at = @now, state = @state, state_reason = 'user', state_changed_at = @now, updated_at = @now WHERE id = @id",
                ct,
                ("@now", now),
                ("@state", Sessions.SessionStateNames.Discarded),
                ("@id", sessionId)).ConfigureAwait(false);

            if (discarded > 0)
            {
                await AuditAsync(sessionId, AuditTypes.SessionDiscarded, frames, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return frames;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// How much of the file is free pages, between 0 and 1.
    ///
    /// SQLite keeps the space a delete frees on a freelist and reuses it, so a store that has purged a
    /// week of sessions is not a store that has to be rebuilt -- the next session's frames go into the
    /// same pages. This is what tells the two apart (2026-09-20 review).
    /// </summary>
    public async Task<double> FreeSpaceFractionAsync(CancellationToken ct = default)
    {
        var pages = await ScalarAsync<long>("PRAGMA page_count", ct).ConfigureAwait(false);
        if (pages <= 0)
        {
            return 0;
        }

        var free = await ScalarAsync<long>("PRAGMA freelist_count", ct).ConfigureAwait(false);
        return (double)free / pages;
    }

    /// <summary>Whether this connection overwrites what it deletes (P2-12). For the test that checks it.</summary>
    internal async Task<bool> SecureDeleteIsOnAsync(CancellationToken ct = default) =>
        await ScalarAsync<long>("PRAGMA secure_delete", ct).ConfigureAwait(false) == 1;

    public async Task VacuumAsync(CancellationToken ct = default)
    {
        // In WAL mode VACUUM writes the rebuilt file into the log; only a truncating checkpoint returns the space.
        await RunAsync("VACUUM", ct).ConfigureAwait(false);
        await RunAsync("PRAGMA wal_checkpoint(TRUNCATE)", ct).ConfigureAwait(false);
    }

    public async Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = await ReadSessionHeaderAsync(sessionId, ct).ConfigureAwait(false);
            if (header is null)
            {
                return null;
            }

            // INV-1: only redacted frames are ever materialized.
            var frames = await ReadFramesAsync(sessionId, ct).ConfigureAwait(false);
            var frameIds = frames.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            var transcript = await ReadTranscriptAsync(sessionId, frameIds, ct).ConfigureAwait(false);
            var segmentIds = transcript.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var events = await ReadEventsAsync(sessionId, frameIds, ct).ConfigureAwait(false);

            var session = header with
            {
                Frames = frames,
                Transcript = transcript,
                Events = events,
                Draft = header.Draft is null ? null : ScrubDraft(header.Draft, frameIds, segmentIds),
            };

            var problems = SessionValidator.Validate(session);
            return problems.Count == 0
                ? session
                : throw new InvalidOperationException($"Stored session '{sessionId}' is inconsistent: {string.Join("; ", problems)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<byte[]?> GetRedactedFrameImageAsync(string frameId, CancellationToken ct = default) =>
        QueryAsync(
            "SELECT image FROM frames WHERE id = @id AND redaction_pending = 0",
            async reader => await reader.ReadAsync(ct).ConfigureAwait(false) ? (byte[])reader[0] : null,
            ct,
            ("@id", frameId));

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var deleted = await ExecuteAsync(_connection, "DELETE FROM sessions WHERE id = @id", ct, ("@id", sessionId)).ConfigureAwait(false);
            if (deleted > 0)
            {
                await AuditAsync(sessionId, AuditTypes.SessionDiscarded, null, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="limit">
    /// How many rows to read. The History screen shows a page at a time, and this used to read every
    /// session the machine had ever recorded -- parsing each one's draft JSON on the way -- so that the
    /// caller could take the first ten. With retention at a week that is the whole store, under the
    /// gate, to fill one screen (2026-09-20 review).
    /// </param>
    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 1000, CancellationToken ct = default) =>
        QueryAsync<IReadOnlyList<SessionSummary>>(
            """
            SELECT s.id, s.started_at, s.duration_ms, s.remote_tool_kind, s.state, s.partial_capture,
                   s.frames_purged_unredacted, s.raw_purged_at,
                   json_extract(s.draft_json, '$.suggested_title'),
                   (SELECT COUNT(*) FROM frames f WHERE f.session_id = s.id AND f.redaction_pending = 0)
            FROM sessions s
            ORDER BY s.started_at DESC
            LIMIT @limit
            """,
            async reader =>
            {
                var rows = new List<SessionSummary>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    // Only the title is read out of the draft, and it is read by the database rather
                    // than by deserialising the note. Turning the whole thing into a DraftNote pulled
                    // every step's text out to show one line, and one field of an unexpected shape threw
                    // and left the row with no title at all — a history saying "Draft" where the
                    // technician had written a name.
                    var title = reader.IsDBNull(8) ? null : reader.GetString(8);

                    rows.Add(new SessionSummary(
                        reader.GetString(0),
                        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt64(5) != 0,
                        reader.GetInt64(6),
                        reader.GetInt32(9),
                        !reader.IsDBNull(7),
                        string.IsNullOrWhiteSpace(title) ? null : title));
                }

                return rows;
            },
            ct,
            ("@limit", limit));

    /// <summary>
    /// The audit log's rows, for one session or for all of them.
    ///
    /// Two statements rather than one with <c>(@session IS NULL OR session_id = @session)</c>. SQLite
    /// cannot use <c>audit_by_session</c> through that OR — it planned a full scan of the log whichever
    /// argument it was given — and the audit log is the one table nothing ever prunes, so it is the one
    /// place a scan gets slower for ever (2026-09-20 review, confirmed with EXPLAIN QUERY PLAN).
    /// </summary>
    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string? sessionId = null, CancellationToken ct = default) =>
        QueryAsync<IReadOnlyList<AuditEntry>>(
            "SELECT id, at, session_id, type, count FROM audit_log "
            + (sessionId is null ? "ORDER BY id" : "WHERE session_id = @session ORDER BY id"),
            async reader =>
            {
                var entries = new List<AuditEntry>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    entries.Add(new AuditEntry(
                        reader.GetInt64(0),
                        ParseIso(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4)));
                }

                return entries;
            },
            ct,
            ("@session", sessionId));

    /// <summary>Every audit row with its chain links, for the export and for verification (ST-045).</summary>
    public async Task<IReadOnlyList<AuditRecord>> GetAuditRecordsAsync(string? sessionId = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadAuditRecordsHoldingGateAsync(sessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The rows, for a caller already holding the gate. See <see cref="VerifyAuditAsync"/>.</summary>
    private Task<IReadOnlyList<AuditRecord>> ReadAuditRecordsHoldingGateAsync(string? sessionId, CancellationToken ct) =>
        QueryInTransactionAsync<IReadOnlyList<AuditRecord>>(
            "SELECT id, at, session_id, type, count, detail, prev_hash, hash FROM audit_log "
            + (sessionId is null ? "ORDER BY id" : "WHERE session_id = @session ORDER BY id"),
            async reader =>
            {
                var records = new List<AuditRecord>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    records.Add(new AuditRecord(
                        reader.GetInt64(0),
                        ParseIso(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7)));
                }

                return records;
            },
            ct,
            ("@session", sessionId));

    /// <summary>
    /// Walks the whole chain and says whether it holds (ST-045).
    ///
    /// Always the whole chain, never one session's rows: a row's hash covers the row before it in the log,
    /// not the row before it in the session, so verifying a filtered view would report a break at every gap
    /// where another session's row sits. The export filters what it shows and verifies what is there.
    /// </summary>
    public async Task<AuditVerification> VerifyAuditAsync(CancellationToken ct = default)
    {
        // Both reads under one hold of the gate. They used to be two: the rows were read, the gate was
        // let go, and the head was read after — so an append landing in that gap made the head describe
        // one more row than the reader had seen, which this method reports as rows deleted from the end.
        // Tampering, on a log nobody had touched, and the store audits every staged frame, so a
        // verification during recording was racing a writer running several times a second (2026-09-20
        // review). A verification that cries wolf is one people stop reading.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await VerifyHoldingGateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AuditVerification> VerifyHoldingGateAsync(CancellationToken ct)
    {
        var records = await ReadAuditRecordsHoldingGateAsync(null, ct).ConfigureAwait(false);
        string? previous = null;
        var checked_ = 0;
        var unchained = 0;

        foreach (var record in records)
        {
            if (record.Hash is null)
            {
                // Written before schema 4. Genuinely unattested rather than broken.
                unchained++;
                continue;
            }

            if (record.PreviousHash != previous || AuditChain.Hash(record, previous) != record.Hash)
            {
                return new AuditVerification(false, checked_, unchained, record.Id);
            }

            previous = record.Hash;
            checked_++;
        }

        // The chain is intact as far as it goes. Whether it goes far enough is a separate question, and
        // the one the chain alone cannot answer: rows deleted from the end leave a shorter chain that
        // verifies perfectly.
        var head = await ReadAuditHeadHoldingGateAsync(ct).ConfigureAwait(false);
        if (head is { } end)
        {
            var have = checked_ + unchained;
            if (have < end.Rows)
            {
                return new AuditVerification(false, checked_, unchained, null, end.Rows - have);
            }

            if (previous is not null && end.Hash.Length > 0 && previous != end.Hash)
            {
                // As many rows as there should be, ending somewhere else. A row replaced wholesale at
                // the end, or a chain rebuilt from a different point.
                return new AuditVerification(false, checked_, unchained, records.Count > 0 ? records[^1].Id : null);
            }
        }

        return new AuditVerification(true, checked_, unchained, null);
    }

    /// <summary>Where the log says it should end, or null on a store older than schema 7.</summary>
    private Task<(long Rows, string Hash)?> ReadAuditHeadHoldingGateAsync(CancellationToken ct) =>
        QueryInTransactionAsync(
            "SELECT rows, hash FROM audit_head WHERE id = 1",
            async reader => await reader.ReadAsync(ct).ConfigureAwait(false)
                ? ((long Rows, string Hash)?)(reader.GetInt64(0), reader.GetString(1))
                : null,
            ct);

    public async Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AuditAsync(sessionId, type, count, detail, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> GetSchemaVersionAsync(CancellationToken ct = default) =>
        ScalarAsync<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations", ct);

    // ---- outbox (ST-064) -------------------------------------------------------------------------------

    /// <summary>
    /// Queues work, unless the same work is already live.
    ///
    /// The uniqueness is the index's, not this method's: two finalizes racing would both see nothing and
    /// both insert, and a check-then-write here would not stop them. The constraint violation is the
    /// answer, and it is the honest one.
    /// </summary>
    public async Task<bool> EnqueueAsync(OutboxItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await RunAsync(
                """
                INSERT INTO outbox (id, session_id, kind, idempotency_key, payload, state, attempts,
                                    created_at, due_at, last_attempt_at, last_error, remote_id)
                VALUES (@id, @session, @kind, @key, @payload, @state, @attempts,
                        @created, @due, @attempted, @error, @remote)
                """,
                ct,
                ("@id", item.Id),
                ("@session", item.SessionId),
                ("@kind", item.Kind.ToString()),
                ("@key", item.IdempotencyKey),
                ("@payload", item.Payload),
                ("@state", Wire(item.State)),
                ("@attempts", item.Attempts),
                ("@created", Iso(item.CreatedAt)),
                ("@due", Iso(item.DueAt)),
                ("@attempted", item.LastAttemptAt is { } at ? Iso(at) : null),
                ("@error", item.LastError),
                ("@remote", item.RemoteId)).ConfigureAwait(false);

            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // The partial unique index refused it: this work is already queued. Not a failure.
            return false;
        }
    }

    public Task<OutboxItem?> TakeDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        QueryAsync(
            """
            SELECT id, session_id, kind, idempotency_key, payload, state, attempts, created_at, due_at,
                   last_attempt_at, last_error, remote_id
            FROM outbox
            WHERE state = 'pending' AND due_at <= @now
            ORDER BY created_at, id
            LIMIT 1
            """,
            async reader => await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadOutbox(reader) : null,
            ct,
            ("@now", Iso(now)));

    public Task<OutboxItem?> TakeUncertainAsync(CancellationToken ct = default) =>
        QueryAsync(
            """
            SELECT id, session_id, kind, idempotency_key, payload, state, attempts, created_at, due_at,
                   last_attempt_at, last_error, remote_id
            FROM outbox
            WHERE state = 'uncertain'
            ORDER BY created_at, id
            LIMIT 1
            """,
            async reader => await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadOutbox(reader) : null,
            ct);

    public Task UpdateAsync(OutboxItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunAsync(
            """
            UPDATE outbox
            SET state = @state, attempts = @attempts, due_at = @due, last_attempt_at = @attempted,
                last_error = @error, remote_id = @remote
            WHERE id = @id
            """,
            ct,
            ("@id", item.Id),
            ("@state", Wire(item.State)),
            ("@attempts", item.Attempts),
            ("@due", Iso(item.DueAt)),
            ("@attempted", item.LastAttemptAt is { } at ? Iso(at) : null),
            ("@error", item.LastError),
            ("@remote", item.RemoteId));
    }

    public Task<OutboxWaiting> CountWaitingAsync(CancellationToken ct = default) =>
        QueryAsync(
            """
            SELECT
                COALESCE(SUM(state = 'pending' AND kind = 'Draft'), 0),
                COALESCE(SUM(state = 'pending' AND kind <> 'Draft'), 0),
                COALESCE(SUM(state = 'uncertain'), 0)
            FROM outbox
            """,
            async reader => await reader.ReadAsync(ct).ConfigureAwait(false)
                ? new OutboxWaiting(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
                : default,
            ct);

    private static OutboxItem ReadOutbox(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Enum.Parse<OutboxKind>(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5) switch
        {
            "pending" => OutboxState.Pending,
            "uncertain" => OutboxState.Uncertain,
            "done" => OutboxState.Done,
            _ => OutboxState.Failed,
        },
        reader.GetInt32(6),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));

    private static string Wire(OutboxState state) => state.ToString().ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // ---- reads -----------------------------------------------------------------------------------------

    private async Task<Session?> ReadSessionHeaderAsync(string sessionId, CancellationToken ct)
    {
        await using var command = Command(
            _connection,
            """
            SELECT id, started_at, remote_tool_kind, remote_tool_version, duration_ms, partial_capture,
                   frames_purged_unredacted, local_only, policy_version, draft_json, suggested_ticket
            FROM sessions WHERE id = @id
            """,
            ("@id", sessionId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Session
        {
            SchemaVersion = SessionValidator.ExpectedSchemaVersion,
            SessionId = reader.GetString(0),
            StartedAt = ParseIso(reader.GetString(1)),
            RemoteTool = new RemoteTool
            {
                Kind = ParseEnum<RemoteToolKind>(reader.GetString(2)),
                ClientVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
            },
            DurationMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            PartialCapture = reader.GetInt64(5) == 1,
            FramesPurgedUnredacted = reader.GetInt64(6),
            LocalOnly = reader.GetInt64(7) == 1,
            PolicyVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
            Draft = reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<DraftNote>(reader.GetString(9), SessionJson.Options),
            SuggestedTicket = reader.IsDBNull(10) ? null : reader.GetString(10),
            Events = [],
            Frames = [],
            Transcript = [],
        };
    }

    public Task<IReadOnlyDictionary<string, long>> GetFrameImageSizesAsync(string sessionId, CancellationToken ct = default) =>
        QueryAsync(
            "SELECT id, length(image) FROM frames WHERE session_id = @session AND redaction_pending = 0",
            async reader =>
            {
                var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    sizes[reader.GetString(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                }

                return (IReadOnlyDictionary<string, long>)sizes;
            },
            ct,
            ("@session", sessionId));

    private async Task<List<Frame>> ReadFramesAsync(string sessionId, CancellationToken ct)
    {
        await using var command = Command(
            _connection,
            """
            SELECT id, ts_ms, trigger, width, height, cursor_x, cursor_y, redacted_at, ocr_text,
                   masked_regions_json, sensitive_context, excluded_by_user
            FROM frames WHERE session_id = @session AND redaction_pending = 0 ORDER BY ts_ms, id
            """,
            ("@session", sessionId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var frames = new List<Frame>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            frames.Add(new Frame
            {
                Id = id,
                TsMs = reader.GetInt64(1),
                Trigger = ParseEnum<FrameTrigger>(reader.GetString(2)),
                Image = $"frames/{id}.jpg",
                Width = reader.GetInt64(3),
                Height = reader.GetInt64(4),
                Cursor = reader.IsDBNull(5) ? null : new Point { X = reader.GetInt64(5), Y = reader.GetInt64(6) },
                RedactionPending = false,
                RedactedAt = ParseIso(reader.GetString(7)),
                OcrText = reader.IsDBNull(8) ? null : reader.GetString(8),
                MaskedRegions = JsonSerializer.Deserialize<List<MaskedRegion>>(reader.GetString(9), SessionJson.Options) ?? [],
                SensitiveContext = reader.GetInt64(10) == 1,
                ExcludedByUser = reader.GetInt64(11) == 1,
            });
        }

        return frames;
    }

    private async Task<List<TranscriptSegment>> ReadTranscriptAsync(string sessionId, HashSet<string> frameIds, CancellationToken ct)
    {
        await using var command = Command(
            _connection,
            "SELECT id, ts_ms, end_ms, speaker, text, frame_id, confidence FROM transcript WHERE session_id = @session ORDER BY ts_ms, id",
            ("@session", sessionId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var segments = new List<TranscriptSegment>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var frameId = reader.IsDBNull(5) ? null : reader.GetString(5);
            segments.Add(new TranscriptSegment
            {
                Id = reader.GetString(0),
                TsMs = reader.GetInt64(1),
                EndMs = reader.GetInt64(2),
                Speaker = ParseEnum<Speaker>(reader.GetString(3)),
                Text = reader.GetString(4),
                FrameId = frameId is not null && frameIds.Contains(frameId) ? frameId : null,
                Confidence = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            });
        }

        return segments;
    }

    private async Task<List<SessionEvent>> ReadEventsAsync(string sessionId, HashSet<string> frameIds, CancellationToken ct)
    {
        await using var command = Command(_connection, "SELECT json FROM events WHERE session_id = @session ORDER BY seq", ("@session", sessionId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var events = new List<SessionEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sessionEvent = JsonSerializer.Deserialize<SessionEvent>(reader.GetString(0), SessionJson.Options)
                ?? throw new InvalidOperationException("A stored event is null.");

            // A frame that was purged or is still pending doesn't exist for readers, so neither does the reference.
            events.Add(sessionEvent switch
            {
                ClickEvent { FrameId: { } f } click when !frameIds.Contains(f) => click with { FrameId = null },
                MarkerEvent { FrameId: { } f } marker when !frameIds.Contains(f) => marker with { FrameId = null },
                _ => sessionEvent,
            });
        }

        return events;
    }

    private static DraftNote ScrubDraft(DraftNote draft, HashSet<string> frameIds, HashSet<string> segmentIds) =>
        draft with
        {
            Steps = draft.Steps.Select(step => step with
            {
                FrameRefs = step.FrameRefs.Where(frameIds.Contains).ToList(),
                TranscriptRefs = step.TranscriptRefs?.Where(segmentIds.Contains).ToList(),
            }).ToList(),
        };

    // ---- plumbing --------------------------------------------------------------------------------------

    private static void EnsureBatteries()
    {
        lock (BatteriesLock)
        {
            if (!_batteriesReady)
            {
                SQLitePCL.Batteries_V2.Init();
                _batteriesReady = true;
            }
        }
    }

    private static async Task KeyAsync(SqliteConnection connection, byte[] key, CancellationToken ct)
    {
        // As bytes, never as a statement: a string holding the key lives until the collector gets to it,
        // which is the one copy the clearing below cannot reach (P1-7).
        StoreKey.Apply(connection, key);
        try
        {
            // The first real read fails with "file is not a database" when the key is wrong.
            await ExecuteAsync(connection, "SELECT count(*) FROM sqlite_master;", ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            throw new StoreKeyException("The store could not be opened with this key.", ex);
        }
    }

    private static async Task MigrateAsync(SqliteConnection connection, TimeProvider time, CancellationToken ct)
    {
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);", ct).ConfigureAwait(false);
        await using var current = Command(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations");
        var applied = Convert.ToInt32(await current.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

        foreach (var (version, sql) in LoadMigrations().Where(m => m.Version > applied))
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, sql, ct).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (@v, @at)",
                ct,
                ("@v", version),
                ("@at", Iso(time.GetUtcNow()))).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(int Version, string Sql)> LoadMigrations()
    {
        var assembly = typeof(SqliteSessionStore).Assembly;
        const string prefix = "ScreenTail.Core.Store.Migrations.";
        var migrations = new List<(int, string)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var file = name[prefix.Length..];
            var version = int.Parse(file.AsSpan(0, file.IndexOf('_', StringComparison.Ordinal)), CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");
            using var reader = new StreamReader(stream);
            migrations.Add((version, reader.ReadToEnd()));
        }

        return migrations.OrderBy(m => m.Item1);
    }

    private async Task<int> RunAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecuteAsync(_connection, sql, ct, parameters).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
        where T : IConvertible
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = Command(_connection, sql, parameters);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture)!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> QueryAsync<T>(string sql, Func<SqliteDataReader, Task<T>> read, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = Command(_connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await read(reader).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="QueryAsync"/> without taking the gate, for callers that already hold it and are inside a
    /// transaction. Taking it again would deadlock on a non-reentrant semaphore.
    /// </summary>
    private async Task<T> QueryInTransactionAsync<T>(string sql, Func<SqliteDataReader, Task<T>> read, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(_connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await read(reader).ConfigureAwait(false);
    }

    private Task<int> AuditAsync(string? sessionId, string type, long? count, CancellationToken ct) =>
        AuditAsync(sessionId, type, count, null, ct);

    /// <summary>
    /// Appends one audit row, linked to the one before it (ST-045).
    ///
    /// The previous hash is read inside the same gate as the insert. Two rows reading the same predecessor
    /// would both claim to follow it, and the chain would fork — which verification would report as
    /// tampering, on a log nobody had touched. Every caller already holds the gate, which is why this reads
    /// through the connection directly rather than through QueryAsync: that would take the same
    /// non-reentrant semaphore and deadlock, as DiscardPendingFrameAsync found the hard way.
    /// </summary>
    private async Task<int> AuditAsync(string? sessionId, string type, long? count, string? detail, CancellationToken ct)
    {
        if (!AuditDetail.IsValid(detail))
        {
            throw new ArgumentException("An audit detail is a short label, never content (INV-10).", nameof(detail));
        }

        string? previous = null;
        await using (var head = Command(_connection, "SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1"))
        {
            var value = await head.ExecuteScalarAsync(ct).ConfigureAwait(false);
            previous = value as string;
        }

        var at = _time.GetUtcNow();
        var hash = AuditChain.Hash(previous, at, sessionId, type, count, detail);

        // The row and the head are one write or neither.
        //
        // They were two separate commits, so anything in between — a crash, a shutdown cancelling the
        // token, and IpcServer hands this its stopping token — left the head describing one row more
        // than the log held. Verification reports that as rows deleted from the end: tampering, on a log
        // nobody had touched, repaired invisibly by the next append (2026-09-20 review).
        //
        // A savepoint rather than a transaction, because most callers are already inside one and
        // SQLite refuses to nest those. Outside a transaction a savepoint opens one; inside, it nests.
        // Either way both statements land together or neither does.
        _ = await ExecuteAsync(_connection, "SAVEPOINT audit_append", ct).ConfigureAwait(false);
        int written;
        try
        {
            written = await AppendHoldingGateAsync(sessionId, type, count, detail, previous, at, hash, ct).ConfigureAwait(false);
        }
        catch
        {
            _ = await ExecuteAsync(_connection, "ROLLBACK TO audit_append", ct).ConfigureAwait(false);
            _ = await ExecuteAsync(_connection, "RELEASE audit_append", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // CancellationToken.None: the two statements are already written, and cancelling the release
        // would leave the savepoint open on a connection every later write shares.
        _ = await ExecuteAsync(_connection, "RELEASE audit_append", CancellationToken.None).ConfigureAwait(false);
        return written;
    }

    /// <summary>The two statements the savepoint above makes atomic.</summary>
    private async Task<int> AppendHoldingGateAsync(
        string? sessionId,
        string type,
        long? count,
        string? detail,
        string? previous,
        DateTimeOffset at,
        string hash,
        CancellationToken ct)
    {
        var written = await ExecuteAsync(
            _connection,
            "INSERT INTO audit_log (at, session_id, type, count, detail, prev_hash, hash) "
            + "VALUES (@at, @session, @type, @count, @detail, @prev, @hash)",
            ct,
            ("@at", Iso(at)),
            ("@session", sessionId),
            ("@type", type),
            ("@count", count),
            ("@detail", detail),
            ("@prev", previous),
            ("@hash", hash)).ConfigureAwait(false);

        // Where the log is supposed to end, written down separately (schema 7). The chain catches an
        // edited or removed row because every hash after it stops matching; it cannot catch rows removed
        // from the end, and a shorter chain verifies perfectly. This is the row count and the last hash,
        // so a truncated log no longer agrees with itself (weaknesses P1-5).
        _ = await ExecuteAsync(
            _connection,
            "INSERT INTO audit_head (id, rows, hash, at) VALUES (1, 1, @hash, @at) "
            + "ON CONFLICT(id) DO UPDATE SET rows = rows + 1, hash = @hash, at = @at",
            ct,
            ("@hash", hash),
            ("@at", Iso(at))).ConfigureAwait(false);

        return written;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Iso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>The schema's snake_case name for an enum member, read off its JsonStringEnumMemberName.</summary>
    private static string EnumName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, SessionJson.Options).Trim('"');

    private static TEnum ParseEnum<TEnum>(string name)
        where TEnum : struct, Enum =>
        JsonSerializer.Deserialize<TEnum>($"\"{name}\"", SessionJson.Options);
}
