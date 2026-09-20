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
    public void AFileThatWillNotGoDoesNotStopTheRest()
    {
        // Antivirus and backup agents hold files open. Aborting on the first one left everything after
        // it on disk, and the list happens to start with the database.
        Populate();
        using var blocked = BlockTokens();

        var result = LocalDataEraser.Erase(_directory);

        // The stored credentials could not go, and everything else did — including the key, which is
        // what makes the session data unreadable whatever else survives.
        Assert.False(result.Complete);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.True(Directory.Exists(Path.Combine(_directory, LocalDataEraser.TokensDirectory)));
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

        using (BlockTokens())
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
    /// Makes the stored-credentials directory refuse to be removed, until the returned handle is let go.
    ///
    /// The real reason differs by platform and neither one travels: on Windows a file held open cannot
    /// be unlinked, and on Unix it can — the bytes simply go when the last handle closes — while a
    /// directory without write permission cannot be emptied. Each arm here is that platform's actual
    /// mechanism rather than a simulation of it. What is under test is what the eraser does when a
    /// delete fails, not why it failed.
    ///
    /// The Unix arm does nothing when the tests run as root, which nothing in this project does.
    /// </summary>
    private IDisposable BlockTokens()
    {
        var tokens = Path.Combine(_directory, LocalDataEraser.TokensDirectory);
        if (OperatingSystem.IsWindows())
        {
            return File.Open(Path.Combine(tokens, "psa.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        }

        return BlockUnix(tokens);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Restores BlockUnix(string tokens)
    {
        var original = File.GetUnixFileMode(tokens);
        File.SetUnixFileMode(tokens, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restores(() => File.SetUnixFileMode(tokens, original));
    }

    private sealed class Restores(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private void Populate()
    {
        foreach (var name in LocalDataEraser.DataFiles)
        {
            File.WriteAllText(Path.Combine(_directory, name), "a customer's session");
        }

        var tokens = Path.Combine(_directory, LocalDataEraser.TokensDirectory);
        Directory.CreateDirectory(tokens);
        File.WriteAllText(Path.Combine(tokens, "psa.bin"), "a credential");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
