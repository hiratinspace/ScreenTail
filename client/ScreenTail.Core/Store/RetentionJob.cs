using ScreenTail.Shared.Ipc;

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
/// <param name="purged">
/// Told of each session whose raw data was just removed (ST-013), so the session machine can stop saying a
/// draft is on its way when the purge took the queued work with it. The host passes
/// <c>SessionMachine.RawDataPurgedAsync</c>.
/// </param>
public sealed class RetentionJob(ISessionStore store, TimeProvider time, RetentionOptions options, Func<string?>? activeSessionId = null, Func<string, CancellationToken, Task<bool>>? purged = null)
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
    /// The session a retention pass must leave alone, read off the machine's snapshot: its id while it
    /// is recording, paused, suppressed or finalizing, and null otherwise.
    ///
    /// The snapshot keeps naming a session after it finalizes, so Review can find the draft. Taken as the
    /// exemption, that kept the last session of the day out of retention, and VACUUM waiting, until the
    /// next session or a restart (INV-12). A finished session ages out like any other.
    /// </summary>
    public static string? SessionBeingRecorded(CaptureStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.State is CaptureStates.Recording or CaptureStates.Paused or CaptureStates.Suppressed or CaptureStates.Finalizing
            ? snapshot.SessionId
            : null;
    }

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
            if (purged is not null)
            {
                _ = await purged(sessionId, ct).ConfigureAwait(false);
            }
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

        // Contents are deliberately nothing: an empty marker owes everything, which is what was asked
        // for. Only a sweep that could not finish writes into it, and then only the names of what it
        // could not remove, never anything about a session (INV-10).
        File.WriteAllBytes(Path.Combine(dataDirectory, PendingMarker), []);
    }

    public static bool IsPending(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, PendingMarker));

    /// <summary>
    /// The line that ends a narrowed marker. A marker cut short by a crash part way through writing it has
    /// no such line, and is read as the original request: everything.
    /// </summary>
    private const string OwedEnd = "end";

    /// <summary>The most of a marker that is read. A longer one is not a list this class wrote.</summary>
    private const int LongestMarker = 64 * 1024;

    /// <summary>
    /// The names whose survival means the service must not start: the store and its key, which a fresh
    /// store would reuse, and every secret <see cref="SecretsFirst"/> removes first, so a device whose
    /// erasure still owes its credential never runs as an activated device. The IPC token is rewritten at
    /// every start, and is on the list anyway, because a token that could not be removed is one that may
    /// not be replaceable either.
    /// </summary>
    private static readonly string[] MustGoBeforeStart =
        [.. SecretsFirst, "ipc.token", "store.db", "store.db-wal", "store.db-shm", "store.db-journal"];

    /// <summary>
    /// What an unfinished erasure still has to remove, by name, or empty when the marker is not narrowed:
    /// none is pending, or the whole of the data directory is still owed.
    ///
    /// An erasure is of what existed when it was asked for (2026-10-12 review, T-59). The marker used to
    /// mean "everything, whenever you next start", so a sweep that could not remove one folder left the
    /// next start deleting every session recorded since. A sweep that cannot finish now writes down the
    /// names of what it could not remove, and the next start removes those and nothing newer. Only names
    /// of entries directly in the data directory go in it, never anything about a session (INV-10).
    /// </summary>
    public static IReadOnlyList<string> StillOwed(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return ReadOwed(dataDirectory) ?? [];
    }

    /// <summary>
    /// Whether the service must not start: an erasure is pending and it still owes the whole directory,
    /// the store, its key, the device's credential or the IPC token. A store opened then would be the old
    /// key under a new file, or the old file under a new key, and the erasure that finishes later would
    /// take whatever was recorded into it; a credential still there would have the service run as an
    /// activated device after the technician asked for everything to go (INV-12). Fails closed: a marker
    /// that cannot be read owes everything, so it blocks.
    /// </summary>
    public static bool BlocksStart(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!IsPending(dataDirectory))
        {
            return false;
        }

        return ReadOwed(dataDirectory) is not { } owed
            || owed.Any(name => MustGoBeforeStart.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finishes an erasure that was asked for and did not happen. Called before the store is opened.
    /// When an earlier sweep narrowed the marker, only what it names is removed.
    /// </summary>
    /// <returns>Null when none was owed, so a caller can tell "nothing to do" from "done".</returns>
    public static ErasureResult? EraseIfPending(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return IsPending(dataDirectory) ? Erase(dataDirectory, AppContext.BaseDirectory, ReadOwed(dataDirectory)) : null;
    }

    /// <summary>
    /// The names a narrowed marker owes, or null when it owes everything. Anything this class would not
    /// have written (a path, a name it keeps, a list with no end line, an unreadable file) is everything:
    /// fail closed, towards the erasure that was promised (INV-12).
    /// </summary>
    private static List<string>? ReadOwed(string dataDirectory)
    {
        string text;
        try
        {
            var marker = new FileInfo(Path.Combine(dataDirectory, PendingMarker));
            if (!marker.Exists || marker.Length == 0 || marker.Length > LongestMarker)
            {
                return null;
            }

            text = File.ReadAllText(marker.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count < 2 || lines[^1] != OwedEnd)
        {
            return null;
        }

        var names = lines[..^1];
        return names.All(IsOwnableName) ? names : null;
    }

    /// <summary>
    /// A name directly in the data directory that an erasure may owe. Never the end line itself, and
    /// never a name that would split into more than one line: either would let a marker cut short by a
    /// crash read as a complete list that leaves something out.
    /// </summary>
    private static bool IsOwnableName(string name) =>
        name.Length is > 0 and <= 255
        && name.Trim() == name
        && name is not "." and not ".." and not OwedEnd
        && name.IndexOfAny(['/', '\\', ':', '\r', '\n']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Equals(KeptDirectory, StringComparison.OrdinalIgnoreCase)
        && !name.Equals(PendingMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Narrows the marker to what is still owed. When the list cannot be written the marker is left as it
    /// was, which owes at least as much.
    /// </summary>
    private static void Narrow(string dataDirectory, List<string> owed)
    {
        if (owed.Count == 0 || !owed.All(IsOwnableName))
        {
            return;
        }

        try
        {
            var names = owed.Distinct(StringComparer.OrdinalIgnoreCase).Append(OwedEnd);
            File.WriteAllText(Path.Combine(dataDirectory, PendingMarker), string.Join('\n', names) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marker as it stood owes the same or more.
        }
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
    internal static ErasureResult Erase(string dataDirectory, string? runningFrom) => Erase(dataDirectory, runningFrom, only: null);

    /// <param name="dataDirectory">The folder to empty.</param>
    /// <param name="runningFrom">Where this program's own files are; see the overload above.</param>
    /// <param name="only">
    /// The names an earlier, unfinished sweep still owes, or null for everything. Whatever was written
    /// beside them since is not part of the erasure that was asked for, and stays.
    /// </param>
    internal static ErasureResult Erase(string dataDirectory, string? runningFrom, IReadOnlyCollection<string>? only)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var deleted = new List<string>();
        var complete = true;
        var refused = new List<string>();
        var program = FolderHolding(dataDirectory, runningFrom);

        foreach (var name in SecretsFirst)
        {
            var path = Path.Combine(dataDirectory, name);
            if (Owed(name) && File.Exists(path))
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
                || entry.Name.Equals(program, StringComparison.OrdinalIgnoreCase)
                || !Owed(entry.Name))
            {
                continue;
            }

            Count(Path.Combine(dataDirectory, entry.Name), Remove(entry));
        }

        if (complete && IsPending(dataDirectory))
        {
            // Last, and only when there is nothing left to owe. While it is there, the next start
            // finishes the job rather than opening what survived.
            var marker = Path.Combine(dataDirectory, PendingMarker);
            Count(marker, Remove(new FileInfo(marker)));
        }
        else if (!complete && IsPending(dataDirectory))
        {
            // Owed is what would not go, and nothing that is written after this.
            Narrow(dataDirectory, refused);
        }

        return new ErasureResult(deleted, complete);

        bool Owed(string name) => only is null || only.Contains(name, StringComparer.OrdinalIgnoreCase);

        void Count(string path, Outcome outcome)
        {
            if (outcome == Outcome.Removed)
            {
                deleted.Add(path);
            }
            else if (outcome == Outcome.Refused)
            {
                complete = false;
                refused.Add(Path.GetFileName(path));
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
