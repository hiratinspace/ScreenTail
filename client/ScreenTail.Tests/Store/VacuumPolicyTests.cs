using System.Security.Cryptography;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Store;

/// <summary>
/// When the store is rebuilt, and when it is left alone (ST-044; 2026-09-20 efficiency review).
///
/// VACUUM rewrites the whole encrypted file: every page decrypted, re-encrypted and written back, with
/// the store's single gate held throughout. Measured at 28.5 seconds over 750 MB on a fast SSD, and it
/// needs free disk space about equal to the database.
///
/// It ran after any retention pass that purged anything. Sessions age out through the working day, so
/// that is a half-minute freeze in the middle of a technician's afternoon — usually to reclaim a handful
/// of pages the next session's frames would have reused anyway.
/// </summary>
public sealed class VacuumPolicyTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    // Nothing expired: nothing was freed, so there is nothing to reclaim.
    [InlineData(0, 0.90, false, double.PositiveInfinity, false)]
    // The common case that was costing half a minute for nothing.
    [InlineData(3, 0.05, false, double.PositiveInfinity, false)]
    // Enough of the file is free that rebuilding it returns something worth having.
    [InlineData(3, 0.50, false, double.PositiveInfinity, true)]
    // ...but not while a session is being recorded. Holding the gate for half a minute stalls the drain
    // loop, frame staging and every IPC read behind it. The space can wait; the session cannot.
    [InlineData(3, 0.90, true, double.PositiveInfinity, false)]
    // ...and not twice in a day, however much is free. Sessions age out through the working day, one
    // retention pass at a time; a rebuild an hour after the last one reclaims what that one just made
    // room for (ST-049, P2-4).
    [InlineData(3, 0.90, false, 2.0, false)]
    [InlineData(3, 0.90, false, 25.0, true)]
    public void TheFileIsRebuiltOnlyWhenThereIsSomethingToReclaimAndNobodyIsRecording(
        int purged, double freeFraction, bool recording, double hoursSinceLastVacuum, bool expected)
    {
        var since = double.IsPositiveInfinity(hoursSinceLastVacuum) ? TimeSpan.MaxValue : TimeSpan.FromHours(hoursSinceLastVacuum);

        Assert.Equal(expected, RetentionJob.ShouldVacuum(purged, freeFraction, recording, since));
    }

    [Fact]
    public async Task PurgingASessionLeavesFreeSpaceTheStoreCanSee()
    {
        // The other half: the fraction the policy reads has to be a real measurement of this file, or
        // the policy is deciding on a number that means nothing.
        var store = await OpenAsync();
        await FinishedSessionAsync(store, "old", frames: 40);
        var before = await store.FreeSpaceFractionAsync();

        await store.PurgeRawDataAsync("old");
        var after = await store.FreeSpaceFractionAsync();

        Assert.True(after > before, $"free space went from {before:P1} to {after:P1} after purging 40 frames");
    }

    [Fact]
    public async Task AFreshStoreHasAlmostNothingToReclaim()
    {
        var store = await OpenAsync();

        Assert.True(await store.FreeSpaceFractionAsync() < 0.3);
    }

    private async Task<SqliteSessionStore> OpenAsync() =>
        _store ??= await SqliteSessionStore.OpenAsync(
            Path.Combine(_dir, "store.db"),
            new FixedKeyProvider(RandomNumberGenerator.GetBytes(32)));

    private static async Task FinishedSessionAsync(SqliteSessionStore store, string id, int frames)
    {
        await store.CreateSessionAsync(new NewSession(id, T0, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        for (var i = 0; i < frames; i++)
        {
            await store.StageFrameAsync(id, new StagedFrame($"{id}-f{i}", i * 1000, FrameTrigger.Click, 1920, 1080, null, new byte[64 * 1024]));
            await store.MarkFrameRedactedAsync($"{id}-f{i}", new RedactionOutcome(new byte[32 * 1024], "text", [], SensitiveContext: false, T0));
        }

        await store.FinalizeSessionAsync(id, new FinalizeInfo(frames * 1000, false));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private SqliteSessionStore? _store;

    private sealed class FixedKeyProvider(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
