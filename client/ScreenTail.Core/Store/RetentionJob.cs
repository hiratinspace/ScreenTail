namespace ScreenTail.Core.Store;

public sealed class RetentionOptions
{
    /// <summary>How long raw data (frames, OCR text, transcript, events) is kept after a session ends. Spec S6: 1–30 days, default 7.</summary>
    /// <summary>Settable: the tenant's policy changes it while the job is running (ST-047).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// INV-12: raw data has a retention limit. Runs at service start and hourly. Sessions whose last activity is
/// older than the retention lose their frames, OCR text, transcript and events; the note text, the session row
/// and the audit log stay, so Session history and published notes are unaffected. The file is rebuilt
/// afterwards when <see cref="ShouldVacuum"/> says so: a third of it free, nobody recording, and not
/// within a day of the last rebuild.
/// </summary>
/// <param name="activeSessionId">
/// The session being recorded right now, exempt while it runs. Everything else ages out, including a session
/// that never reached finalize — otherwise a crash whose recovery never ran would keep raw frames for good.
/// </param>
public sealed class RetentionJob(ISessionStore store, TimeProvider time, RetentionOptions options, Func<string?>? activeSessionId = null)
{
    /// <summary>
    /// How much of the file has to be free before rebuilding it is worth the freeze.
    ///
    /// A third. SQLite reuses freed pages, so a store that has just purged a week of sessions is not a
    /// store that needs rebuilding -- the next session's frames go into the same space. Below this the
    /// file is doing its own housekeeping and the only thing a VACUUM buys is a smaller number in
    /// Explorer.
    /// </summary>
    public const double WorthReclaiming = 0.3;

    /// <summary>
    /// How long the file is left alone after a rebuild, whatever the numbers say.
    ///
    /// Sessions age out through the working day, one retention pass at a time, and each pass that
    /// purged something used to be allowed its own rebuild. The second rebuild of a day reclaims the
    /// space the first one just made room for, at the same cost as the first (ST-049, P2-4).
    /// </summary>
    public static readonly TimeSpan AtMostEvery = TimeSpan.FromDays(1);

    private DateTimeOffset? _lastVacuum;

    /// <summary>
    /// Whether to rebuild the file.
    ///
    /// VACUUM rewrites every page of an encrypted database -- decrypt, re-encrypt, write -- with the
    /// store's single gate held for all of it: 28.5 seconds over 750 MB on a fast SSD, and it wants free
    /// disk space about equal to the database. It ran after any retention pass that purged anything, and
    /// sessions age out through the working day, so that was a half-minute freeze in the middle of a
    /// technician's afternoon to reclaim pages that were about to be reused (2026-09-20 review).
    ///
    /// Not while recording, whatever the numbers say. Holding the gate that long stalls the drain loop,
    /// frame staging and every IPC read behind them; the space can wait and the session cannot.
    /// </summary>
    public static bool ShouldVacuum(int purged, double freeFraction, bool recording, TimeSpan sinceLastVacuum) =>
        purged > 0 && !recording && freeFraction >= WorthReclaiming && sinceLastVacuum >= AtMostEvery;

    /// <returns>How many sessions were purged in this run.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - options.Retention;
        var expired = await store.ListSessionsWithRawDataOlderThanAsync(cutoff, activeSessionId?.Invoke(), ct).ConfigureAwait(false);
        foreach (var sessionId in expired)
        {
            await store.PurgeRawDataAsync(sessionId, ct).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        if (ShouldVacuum(
            expired.Count,
            await store.FreeSpaceFractionAsync(ct).ConfigureAwait(false),
            recording: activeSessionId?.Invoke() is not null,
            sinceLastVacuum: _lastVacuum is { } last ? now - last : TimeSpan.MaxValue))
        {
            await store.VacuumAsync(ct).ConfigureAwait(false);
            _lastVacuum = now;
        }

        return expired.Count;
    }
}

/// <summary>
/// "Delete everything now" (Spec S6, INV-12): the store, its key, this run's IPC token and any saved
/// credentials under the data directory. Downloaded models stay; they hold no user data. The store must
/// be closed first: on Windows an open database file cannot be deleted.
/// </summary>
/// <param name="Deleted">What went, in the order it went. The key is first on purpose.</param>
/// <param name="Complete">
/// False when something could not be removed. The erasure is still owed, and the marker stays.
/// </param>
public sealed record ErasureResult(IReadOnlyList<string> Deleted, bool Complete);

public static class LocalDataEraser
{
    /// <summary>
    /// Everything "delete everything" has to remove (INV-12).
    ///
    /// <b>The key is first.</b> The store is encrypted, so removing the key makes all of it unreadable
    /// in one step — which is the most that can be promised when the step after it might fail. The
    /// database used to be first, so a database held open by antivirus left a readable store and a key
    /// beside it, and the technician had been told it was gone.
    ///
    /// The two with -journal and .tmp are not hypothetical: SQLite falls back to a rollback journal
    /// wherever WAL cannot be enabled, which is any redirected or network profile, and the token's
    /// temporary file survives a failure between writing it and restricting it. Both would otherwise be
    /// left holding real data after the user asked for all of it to go.
    /// </summary>
    public static readonly string[] DataFiles =
    [
        "store.key",
        "store.db",
        "store.db-wal",
        "store.db-shm",
        "store.db-journal",
        "ipc.token",
        "ipc.token.tmp",
    ];

    public const string TokensDirectory = "tokens";

    /// <summary>
    /// Says that an erasure has been asked for, before anybody is told it happened.
    ///
    /// The command used to answer the UI, and then the service stopped, and only then did anything get
    /// deleted — in a finally, after every other shutdown step. A process that exited first, or a file
    /// it could not remove, left the store intact and no record anywhere that erasure was owed. The
    /// next start opened it and carried on (2026-09-19 review).
    /// </summary>
    public const string PendingMarker = "erase-everything.pending";

    public static void MarkPending(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);

        // Contents are deliberately nothing: its existence is the whole message, and a file in a
        // directory that is about to be deleted is no place for anything about a session (INV-10).
        File.WriteAllBytes(Path.Combine(dataDirectory, PendingMarker), []);
    }

    public static bool IsPending(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, PendingMarker));

    /// <summary>
    /// Finishes an erasure that was asked for and did not happen. Called before the store is opened.
    /// </summary>
    /// <returns>Null when none was owed, so a caller can tell "nothing to do" from "done".</returns>
    public static ErasureResult? EraseIfPending(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return IsPending(dataDirectory) ? Erase(dataDirectory) : null;
    }

    /// <summary>
    /// Removes everything, and keeps going when one thing will not go.
    ///
    /// Aborting on the first failure left everything after it on disk, and the list began with the
    /// database. A file held open by antivirus or a backup agent is the ordinary case, not a rare one.
    /// </summary>
    public static ErasureResult Erase(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var deleted = new List<string>();
        var complete = true;

        foreach (var name in DataFiles)
        {
            var path = Path.Combine(dataDirectory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            if (TryDelete(() => File.Delete(path)))
            {
                deleted.Add(path);
            }
            else
            {
                complete = false;
            }
        }

        var tokens = Path.Combine(dataDirectory, TokensDirectory);
        if (Directory.Exists(tokens))
        {
            if (TryDelete(() => Directory.Delete(tokens, recursive: true)))
            {
                deleted.Add(tokens);
            }
            else
            {
                complete = false;
            }
        }

        // Last, and only when there is nothing left to owe. While it is there, the next start finishes
        // the job rather than opening what survived.
        if (complete && IsPending(dataDirectory))
        {
            var marker = Path.Combine(dataDirectory, PendingMarker);
            if (TryDelete(() => File.Delete(marker)))
            {
                deleted.Add(marker);
            }
        }

        return new ErasureResult(deleted, complete);
    }

    private static bool TryDelete(Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open, or not ours to remove. Counted rather than thrown: the caller's job is to keep
            // going and to leave the erasure owed.
            return false;
        }
    }
}

