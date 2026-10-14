using ScreenTail.Core.Store;

namespace ScreenTail.Tests.Store;

/// <summary>
/// INV-12's second half: "delete everything" actually deletes everything (2026-09-19 review).
///
/// The command answered the UI, and then the service stopped, and only then did anything get deleted —
/// in a <c>finally</c>, after every other shutdown step had run. A file held open by antivirus or a
/// backup agent, or a process that exited before it got there, left the store intact with no record
/// anywhere that erasure had been asked for. The next start opened it and carried on, and the
/// technician had been told it was gone.
///
/// Two things fix that. The intent is written down before anything is answered, so a start that finds
/// the marker finishes the job. And the key goes first: the store is encrypted, so one small delete
/// makes all of it unreadable, which is the most that can be promised in a single step.
/// </summary>
public sealed class EraseEverythingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "screentail-erase-" + Guid.NewGuid().ToString("N")[..8]);

    public EraseEverythingTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void EverythingTheStoreIsMadeOfGoes()
    {
        Populate();

        var result = LocalDataEraser.Erase(_directory);

        Assert.True(result.Complete);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void TheKeyGoesFirst()
    {
        // The store is encrypted, so the key is the one delete that makes all of it unreadable. If the
        // database is locked and the key is not, the technician still gets what they asked for; the
        // other order leaves a readable database behind.
        Populate();

        var deleted = LocalDataEraser.Erase(_directory).Deleted;

        Assert.Equal("store.key", Path.GetFileName(deleted[0]));
    }

    [Fact]
    public void TheDevicesRefreshTokenGoesStraightAfterTheKey()
    {
        // The other secret. With the key gone the store is unreadable; with the refresh token gone the
        // device can no longer ask the backend for anything. Both before the slow part, so a sweep that
        // stalls on a locked database has already done what matters most.
        Populate();

        var deleted = LocalDataEraser.Erase(_directory).Deleted.Select(Path.GetFileName).ToArray();

        Assert.Equal(["store.key", "device.cred", "device.cred.tmp"], deleted[..3]);
    }

    [Fact]
    public void EveryFileTheServiceWritesUnderTheDataDirectoryIsErasedOrDeliberatelyKept()
    {
        // The names are written out here rather than read from the eraser, which is how the device's
        // refresh token and the settings files went missing for a month: the fixture was built from the
        // same list it was checking, so a file the list forgot was never created either (2026-10-12
        // review, S-16 and T-57). Each name is the one its producer uses.
        string[] written =
        [
            "store.key",                  // DpapiKeyProvider.DefaultKeyFilePath
            "store.db",                   // CaptureHost, the store
            "store.db-wal",               // SQLite, WAL mode
            "store.db-shm",               // SQLite, WAL mode
            "store.db-journal",           // SQLite, rollback journal where WAL is refused
            "device.cred",                // DpapiDeviceCredentials.DefaultPath: the refresh token
            "device.cred.tmp",            // DpapiDeviceCredentials.Save writes this, then moves it
            "ipc.token",                  // IpcTokenFile.DefaultPath
            "ipc.token.tmp",              // IpcTokenFile.Write
            "settings.json",              // CaptureHost, ClientSettingsStore
            "settings.json.tmp",
            "capture.json",               // CaptureHost, CaptureSettingsStore
            "capture.json.tmp",
            "style.json",                 // CaptureHost, StyleStore
            "style.json.tmp",
            "policy.json",                // CaptureHost, FilePolicyCache
            "policy.json.tmp",
            "shell.json",                 // ShellPreferencesStore.DefaultPath, written by the UI
            "shell.json.tmp",
            LocalDataEraser.PendingMarker,
        ];
        foreach (var name in written)
        {
            File.WriteAllText(Path.Combine(_directory, name), "a customer's session");
        }

        Directory.CreateDirectory(Path.Combine(_directory, "crashes")); // CrashReport.DefaultDirectory
        File.WriteAllText(Path.Combine(_directory, "crashes", "ScreenTail.Service-20261012.txt"), "a stack");
        Directory.CreateDirectory(Path.Combine(_directory, "models")); // WhisperRecogniser's default
        File.WriteAllText(Path.Combine(_directory, "models", "ggml-base.bin"), "weights");

        var result = LocalDataEraser.Erase(_directory);

        // The downloaded speech model is the one thing kept on purpose: it holds nothing about anybody,
        // and it is hundreds of megabytes to fetch again.
        Assert.True(result.Complete);
        Assert.Equal(["models"], Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName));
        Assert.Equal(["ggml-base.bin"], Directory.GetFileSystemEntries(Path.Combine(_directory, "models")).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileNobodyHasListedYetGoesToo()
    {
        // The next thing a ticket writes beside the store is erased without anyone remembering to add
        // it here. Keeping is the exception that has to be named; deleting is the default.
        File.WriteAllText(Path.Combine(_directory, "something-a-later-version-writes.json"), "a customer's name");
        Directory.CreateDirectory(Path.Combine(_directory, "cache", "thumbnails"));
        File.WriteAllText(Path.Combine(_directory, "cache", "thumbnails", "f1.png"), "a screenshot");

        var result = LocalDataEraser.Erase(_directory);

        Assert.True(result.Complete);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void ALinkInTheDataDirectoryGoesAndWhatItPointsAtStays()
    {
        // The sweep removes whatever is in the folder, so it must never walk out of it. A link to
        // somewhere else is removed as a link; the folder it points at is not ours to empty.
        var elsewhere = _directory + "-elsewhere";
        Directory.CreateDirectory(elsewhere);
        try
        {
            File.WriteAllText(Path.Combine(elsewhere, "not-ours.txt"), "somebody else's file");
            Directory.CreateSymbolicLink(Path.Combine(_directory, "linked-folder"), elsewhere);
            File.CreateSymbolicLink(Path.Combine(_directory, "linked-file"), Path.Combine(elsewhere, "not-ours.txt"));
            Directory.CreateDirectory(Path.Combine(_directory, "crashes"));
            Directory.CreateSymbolicLink(Path.Combine(_directory, "crashes", "linked-deeper"), elsewhere);

            var result = LocalDataEraser.Erase(_directory);

            Assert.True(result.Complete);
            Assert.Empty(Directory.GetFileSystemEntries(_directory));
            Assert.Equal("somebody else's file", File.ReadAllText(Path.Combine(elsewhere, "not-ours.txt")));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void AFileThatWillNotGoDoesNotStopTheRest()
    {
        // Antivirus and backup agents hold files open. Aborting on the first one left everything after
        // it on disk, and the list happens to start with the database.
        Populate();
        using var blocked = BlockCrashes();

        var result = LocalDataEraser.Erase(_directory);

        // The crash reports could not go, and everything else did: the key, which is what makes the
        // session data unreadable whatever else survives, and the device's refresh token with it.
        Assert.False(result.Complete);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.True(Directory.Exists(Path.Combine(_directory, Crashes)));
    }

    [Fact]
    public void AnErasureThatWasAskedForIsStillOwedAfterACrash()
    {
        // The whole point of writing the intent down. The service was told to erase, said yes, and died
        // before it could — or could not delete a locked file. The next start must not simply open the
        // store and carry on.
        Populate();
        LocalDataEraser.MarkPending(_directory);

        Assert.True(LocalDataEraser.IsPending(_directory));

        var result = Assert.IsType<ErasureResult>(LocalDataEraser.EraseIfPending(_directory));

        Assert.True(result.Complete);
        Assert.False(LocalDataEraser.IsPending(_directory));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void AnErasureNobodyAskedForDoesNotHappenOnStart()
    {
        // The control. A marker that is read wrongly, or a check that is not made at all, would mean
        // every start deletes the technician's work.
        Populate();

        var result = LocalDataEraser.EraseIfPending(_directory);

        Assert.Null(result);
        Assert.NotEmpty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void AnErasureThatCouldNotFinishIsStillOwedNextTime()
    {
        Populate();
        LocalDataEraser.MarkPending(_directory);

        using (BlockCrashes())
        {
            var blocked = Assert.IsType<ErasureResult>(LocalDataEraser.EraseIfPending(_directory));

            Assert.False(blocked.Complete);
            Assert.True(LocalDataEraser.IsPending(_directory));
        }

        // Whatever was holding it lets go, and the next start finishes the job.
        var finished = Assert.IsType<ErasureResult>(LocalDataEraser.EraseIfPending(_directory));

        Assert.True(finished.Complete);
        Assert.False(LocalDataEraser.IsPending(_directory));
    }

    [Fact]
    public void TheMarkerItselfIsNotLeftBehind()
    {
        Populate();
        LocalDataEraser.MarkPending(_directory);

        _ = LocalDataEraser.EraseIfPending(_directory);

        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void ErasingADirectoryThatIsAlreadyGoneIsNotAnError()
    {
        Directory.Delete(_directory, recursive: true);

        var result = LocalDataEraser.Erase(_directory);

        Assert.True(result.Complete);
        Assert.Empty(result.Deleted);
    }

    /// <summary>
    /// Makes the crash-report directory refuse to be removed, until the returned handle is let go.
    ///
    /// The real reason differs by platform and neither one travels: on Windows a file held open cannot
    /// be unlinked, and on Unix it can — the bytes simply go when the last handle closes — while a
    /// directory without write permission cannot be emptied. Each arm here is that platform's actual
    /// mechanism rather than a simulation of it. What is under test is what the eraser does when a
    /// delete fails, not why it failed.
    ///
    /// The Unix arm does nothing when the tests run as root, which nothing in this project does.
    /// </summary>
    private IDisposable BlockCrashes()
    {
        var crashes = Path.Combine(_directory, Crashes);
        if (OperatingSystem.IsWindows())
        {
            return File.Open(Path.Combine(crashes, "report.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        }

        return BlockUnix(crashes);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Restores BlockUnix(string directory)
    {
        var original = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restores(() => File.SetUnixFileMode(directory, original));
    }

    private sealed class Restores(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>The crash-report queue's folder, CrashReport.DefaultDirectory's last part.</summary>
    private const string Crashes = "crashes";

    /// <summary>
    /// What a device that has been used looks like, by the names the producers write. Deliberately not
    /// read from the eraser: a fixture built from the list under test cannot see what the list forgot.
    /// </summary>
    private void Populate()
    {
        foreach (var name in (string[])["store.key", "store.db", "store.db-wal", "device.cred", "device.cred.tmp", "ipc.token", "settings.json", "policy.json", "shell.json"])
        {
            File.WriteAllText(Path.Combine(_directory, name), "a customer's session");
        }

        var crashes = Path.Combine(_directory, Crashes);
        Directory.CreateDirectory(crashes);
        File.WriteAllText(Path.Combine(crashes, "report.txt"), "a stack");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
