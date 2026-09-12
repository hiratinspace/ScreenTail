namespace ScreenTail.Core.Store;

public sealed class RetentionOptions
{
    /// <summary>How long raw data (frames, OCR text, transcript, events) is kept after a session ends. Spec S6: 1–30 days, default 7.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>
/// INV-12: raw data has a retention limit. Runs at service start and hourly. Sessions whose last activity is
/// older than the retention lose their frames, OCR text, transcript and events; the note text, the session row
/// and the audit log stay, so Session history and published notes are unaffected. The file is vacuumed
/// afterwards so the space is actually returned.
/// </summary>
/// <param name="activeSessionId">
/// The session being recorded right now, exempt while it runs. Everything else ages out, including a session
/// that never reached finalize — otherwise a crash whose recovery never ran would keep raw frames for good.
/// </param>
public sealed class RetentionJob(ISessionStore store, TimeProvider time, RetentionOptions options, Func<string?>? activeSessionId = null)
{
    /// <returns>How many sessions were purged in this run.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - options.Retention;
        var expired = await store.ListSessionsWithRawDataOlderThanAsync(cutoff, activeSessionId?.Invoke(), ct).ConfigureAwait(false);
        foreach (var sessionId in expired)
        {
            await store.PurgeRawDataAsync(sessionId, ct).ConfigureAwait(false);
        }

        if (expired.Count > 0)
        {
            await store.VacuumAsync(ct).ConfigureAwait(false);
        }

        return expired.Count;
    }
}

/// <summary>
/// "Delete everything now" (Spec S6, INV-12): the store, its key, this run's IPC token and any saved
/// credentials under the data directory. Downloaded models stay; they hold no user data. The store must
/// be closed first: on Windows an open database file cannot be deleted.
/// </summary>
public static class LocalDataEraser
{
    public static readonly string[] DataFiles = ["store.db", "store.db-wal", "store.db-shm", "store.key", "ipc.token"];

    public const string TokensDirectory = "tokens";

    /// <returns>The paths that were deleted.</returns>
    public static IReadOnlyList<string> Erase(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var deleted = new List<string>();
        foreach (var name in DataFiles)
        {
            var path = Path.Combine(dataDirectory, name);
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted.Add(path);
            }
        }

        var tokens = Path.Combine(dataDirectory, TokensDirectory);
        if (Directory.Exists(tokens))
        {
            Directory.Delete(tokens, recursive: true);
            deleted.Add(tokens);
        }

        return deleted;
    }
}
