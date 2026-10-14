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
/// "Delete everything now" (Spec S6, INV-12): everything under the data directory, which is the store, its
/// key, the device's refresh token, this run's IPC token, the settings and the crash reports. Downloaded
/// models stay; they hold no user data. The store must be closed first: on Windows an open database file
/// cannot be deleted.
/// </summary>
/// <param name="Deleted">What went, in the order it went. The key is first on purpose.</param>
/// <param name="Complete">
/// False when something could not be removed. The erasure is still owed, and the marker stays.
/// </param>
public sealed record ErasureResult(IReadOnlyList<string> Deleted, bool Complete);

public static class LocalDataEraser
{
    /// <summary>
    /// What goes before anything else, in this order (INV-12).
    ///
    /// <b>The key is first.</b> The store is encrypted, so removing the key makes all of it unreadable
    /// in one step, which is the most that can be promised when the step after it might fail. The
    /// database used to be first, so a database held open by antivirus left a readable store and a key
    /// beside it, and the technician had been told it was gone.
    ///
    /// <b>The device's refresh token is next</b> (DpapiDeviceCredentials), with the temporary file a save
    /// writes before moving it into place. Without it the device can ask the backend for nothing, and
    /// the next start finds it not activated. It used to be left behind altogether.
    /// </summary>
    private static readonly string[] SecretsFirst = ["store.key", "device.cred", "device.cred.tmp"];

    /// <summary>
    /// The one thing kept: the downloaded speech models. Nothing about anybody is in them, and they are
    /// hundreds of megabytes to fetch again.
    ///
    /// Everything else under the data directory goes, whatever its name. This used to be a list of the
    /// files the eraser knew about, and it did not know about the refresh token, the settings, the
    /// cached policy, the UI's preferences or the crash reports (2026-10-12 review). A file a later
    /// version writes is erased without anybody having to remember to add it here: keeping is the
    /// exception that has to be named, and deleting is the default.
    /// </summary>
    public const string KeptDirectory = "models";

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
    public static ErasureResult Erase(string dataDirectory) => Erase(dataDirectory, AppContext.BaseDirectory);

    /// <param name="dataDirectory">The folder to empty.</param>
    /// <param name="runningFrom">
    /// Where this program's own files are. A folder in the data directory that holds them is kept whole,
    /// like the models: deleting a running program fails part way, leaves a copy that will not start, and
    /// keeps the erasure owed on every start after, which wipes each new session too.
    /// </param>
    internal static ErasureResult Erase(string dataDirectory, string? runningFrom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var deleted = new List<string>();
        var complete = true;
        var program = FolderHolding(dataDirectory, runningFrom);

        foreach (var name in SecretsFirst)
        {
            var path = Path.Combine(dataDirectory, name);
            if (File.Exists(path))
            {
                Count(path, Remove(new FileInfo(path)));
            }
        }

        // Read in full before anything is removed, rather than deleting from under an enumeration.
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dataDirectory).GetFileSystemInfos();
        }
        catch (DirectoryNotFoundException)
        {
            entries = [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing can be listed, so nothing can be said to be gone. Owed until the next start.
            return new ErasureResult(deleted, Complete: false);
        }

        foreach (var entry in entries)
        {
            if (entry.Name.Equals(KeptDirectory, StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals(PendingMarker, StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals(program, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Count(Path.Combine(dataDirectory, entry.Name), Remove(entry));
        }

        // Last, and only when there is nothing left to owe. While it is there, the next start finishes
        // the job rather than opening what survived.
        if (complete && IsPending(dataDirectory))
        {
            var marker = Path.Combine(dataDirectory, PendingMarker);
            Count(marker, Remove(new FileInfo(marker)));
        }

        return new ErasureResult(deleted, complete);

        void Count(string path, Outcome outcome)
        {
            if (outcome == Outcome.Removed)
            {
                deleted.Add(path);
            }
            else if (outcome == Outcome.Refused)
            {
                complete = false;
            }
        }
    }

    /// <summary>
    /// The name of the folder directly in the data directory that the program runs from, or null when it
    /// runs from somewhere else. Paths are compared as Windows compares them, ignoring case.
    /// </summary>
    private static string? FolderHolding(string dataDirectory, string? runningFrom)
    {
        if (string.IsNullOrEmpty(runningFrom))
        {
            return null;
        }

        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)) + Path.DirectorySeparatorChar;
        var running = Path.GetFullPath(runningFrom);
        if (running.Length <= data.Length || !running.StartsWith(data, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return running[data.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0];
    }

    internal enum Outcome
    {
        Removed,
        AlreadyGone,
        Refused,
    }

    /// <summary>
    /// Whether an entry is still in the folder, asked afresh. A link counts while it is there, whether or
    /// not what it points at is. When the answer cannot be had, it is "still there", so the erasure stays
    /// owed rather than being called done.
    /// </summary>
    internal static bool IsStillThere(FileSystemInfo entry)
    {
        try
        {
            entry.Refresh();
            return entry.Exists || entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Removes one entry, and never anything outside the data directory: a folder goes with everything in
    /// it, and a link goes as a link. .NET removes a link to a folder without following it, at the top
    /// and on the way down, on Windows and on Unix alike, so whatever it points at is left alone.
    /// </summary>
    private static Outcome Remove(FileSystemInfo entry)
    {
        var outcome = TryRemove(entry);

        // Windows will not delete anything marked read-only, and a restore from backup or a copy off
        // removable media can leave that mark on anything. Refused over it, the erasure would stay owed
        // and run again on every start, after a new activation too. The mark is this account's own to
        // clear, so it is cleared and the delete tried once more. Only after a refusal: the usual erase
        // pays nothing for it. Unix has no such mark, and a folder without write permission is a refusal.
        if (outcome == Outcome.Refused && OperatingSystem.IsWindows() && ClearReadOnly(entry))
        {
            outcome = TryRemove(entry);
        }

        return outcome;
    }

    private static Outcome TryRemove(FileSystemInfo entry) => TryRemove(entry, () =>
    {
        if (entry is DirectoryInfo folder)
        {
            folder.Delete(recursive: true);
        }
        else
        {
            entry.Delete();
        }
    });

    internal static Outcome TryRemove(FileSystemInfo entry, Action delete)
    {
        try
        {
            delete();
            return Outcome.Removed;
        }
        catch (DirectoryNotFoundException)
        {
            // Usually gone between being listed and being removed, and then nothing is owed for it. But a
            // recursive delete can say this part way down, about something inside, so it counts as gone
            // only when it really is.
            return IsStillThere(entry) ? Outcome.Refused : Outcome.AlreadyGone;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open, or not ours to remove. Counted rather than thrown: the caller's job is to keep
            // going and to leave the erasure owed.
            return Outcome.Refused;
        }
    }

    /// <summary>
    /// Clears the read-only mark on an entry and on everything inside it. Links are neither changed nor
    /// followed, so nothing outside the data directory is touched.
    /// </summary>
    /// <returns>Whether anything was cleared, which is whether another try can go differently.</returns>
    private static bool ClearReadOnly(FileSystemInfo entry)
    {
        var cleared = false;
        try
        {
            entry.Refresh();
            cleared = ClearOne(entry);
            if (entry is DirectoryInfo folder && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var inside = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = true,
                };
                foreach (var item in folder.EnumerateFileSystemInfos("*", inside))
                {
                    cleared |= ClearOne(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could be cleared was; the retry says whether it was enough.
        }

        return cleared;

        static bool ClearOne(FileSystemInfo item)
        {
            var attributes = item.Attributes;
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != FileAttributes.ReadOnly)
            {
                return false;
            }

            item.Attributes = attributes & ~FileAttributes.ReadOnly;
            return true;
        }
    }
}
