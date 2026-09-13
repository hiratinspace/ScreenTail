using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenTail.Core.Audit;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Store;

/// <summary>
/// <see cref="ISessionStore"/> on SQLite + SQLCipher. One connection, commands serialized, WAL journal.
/// Open with <see cref="OpenAsync"/>; it keys the connection and applies pending migrations.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IAuditLog
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
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
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
            INSERT INTO sessions (id, started_at, remote_tool_kind, remote_tool_version, local_only, policy_version, created_at, updated_at)
            VALUES (@id, @started, @kind, @version, @local, @policy, @now, @now)
            """,
            ct,
            ("@id", session.SessionId),
            ("@started", Iso(session.StartedAt)),
            ("@kind", EnumName(session.RemoteTool.Kind)),
            ("@version", session.RemoteTool.ClientVersion),
            ("@local", session.LocalOnly ? 1 : 0),
            ("@policy", session.PolicyVersion),
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

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string? sessionId = null, CancellationToken ct = default) =>
        QueryAsync<IReadOnlyList<AuditEntry>>(
            "SELECT id, at, session_id, type, count FROM audit_log WHERE (@session IS NULL OR session_id = @session) ORDER BY id",
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
    public Task<IReadOnlyList<AuditRecord>> GetAuditRecordsAsync(string? sessionId = null, CancellationToken ct = default) =>
        QueryAsync<IReadOnlyList<AuditRecord>>(
            "SELECT id, at, session_id, type, count, detail, prev_hash, hash FROM audit_log "
            + "WHERE (@session IS NULL OR session_id = @session) ORDER BY id",
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
        var records = await GetAuditRecordsAsync(ct: ct).ConfigureAwait(false);
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

        return new AuditVerification(true, checked_, unchained, null);
    }

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
                   frames_purged_unredacted, local_only, policy_version, draft_json
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
            Events = [],
            Frames = [],
            Transcript = [],
        };
    }

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
        // A raw key ("x'...'") skips SQLCipher's passphrase KDF; the key already has full entropy.
        var hex = Convert.ToHexString(key);
        await ExecuteAsync(connection, $"PRAGMA key = \"x'{hex}'\";", ct).ConfigureAwait(false);
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
            previous = value is string hash ? hash : null;
        }

        var at = _time.GetUtcNow();
        return await ExecuteAsync(
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
            ("@hash", AuditChain.Hash(previous, at, sessionId, type, count, detail))).ConfigureAwait(false);
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
