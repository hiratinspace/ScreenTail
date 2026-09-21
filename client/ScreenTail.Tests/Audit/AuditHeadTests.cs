using System.Security.Cryptography;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Store;

namespace ScreenTail.Tests.Audit;

/// <summary>
/// The audit head and the log it describes, kept in step (ST-045; 2026-09-20 review).
///
/// Schema 7 records where the log is supposed to end, because a chain alone cannot notice rows removed
/// from its end: a shorter chain verifies perfectly. The head is the second opinion.
///
/// Two ways it disagreed with an honest log. The row and the head were written as two separate commits,
/// so anything between them — a crash, a shutdown cancelling the token — left the head one behind. And
/// verification read the rows and the head under two separate acquisitions of the store's gate, so an
/// append landing in the gap made a log nobody had touched report as tampered.
///
/// <b>A verification that cries wolf is one people stop reading</b>, which costs more than not having it.
/// </summary>
public sealed class AuditHeadTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"st-audit-head-{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private SqliteSessionStore? _store;

    [Fact]
    public async Task VerifyingWhileTheLogIsBeingWrittenDoesNotCryWolf()
    {
        // The race, run until it bites. Verification used to read the rows, let go of the gate, then read
        // the head: an append in that gap made the head describe one more row than the reader had seen,
        // which reads as "somebody deleted the end of the log".
        var store = await OpenAsync();
        var ct = TestContext.Current.CancellationToken;

        var writing = Task.Run(
            async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    await store.RecordAsync(AuditTypes.FrameRedacted, "s1", 1, ct: ct);
                }
            },
            ct);

        var verdicts = new List<AuditVerification>();
        while (!writing.IsCompleted)
        {
            verdicts.Add(await store.VerifyAuditAsync(ct));
        }

        await writing;
        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, v => Assert.True(v.Intact, $"An untouched log verified false: missing {v.Missing}, broken at {v.BrokenAt}."));
    }

    [Fact]
    public async Task TheHeadCountsExactlyWhatIsInTheLog()
    {
        // The invariant the head exists to carry. Checked after appends made directly and after ones made
        // inside a larger transaction, because those take a different path through the store.
        var store = await OpenAsync();
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(AuditTypes.FrameRedacted, "s1", 1, ct: ct);
        }

        var records = await store.GetAuditRecordsAsync(ct: ct);
        var verdict = await store.VerifyAuditAsync(ct);

        Assert.True(verdict.Intact);
        Assert.Equal(0L, verdict.Missing);
        Assert.Equal(records.Count, verdict.Checked + verdict.Unchained);
    }

    private async Task<SqliteSessionStore> OpenAsync() => _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

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
