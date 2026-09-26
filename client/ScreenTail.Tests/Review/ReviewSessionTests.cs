using System.Security.Cryptography;
using ScreenTail.Core.Review;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>ST-074, against the real store: "edits survive restart", and what Discard actually destroys.</summary>
public sealed class ReviewSessionTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SqliteSessionStore> _stores = [];

    [Fact]
    public async Task AnEditSurvivesTheProcessThatMadeIt()
    {
        // The AC's second half, and the half a unit test of the editor cannot reach: the write has to land
        // in the encrypted store and come back as the same note through a different connection.
        var store = await OpenAsync();
        await SeedAsync(store);
        var review = new ReviewSession(store, "s1");

        var note = new NoteDraft((await review.LoadAsync())!.Draft!);
        note.SetProblem("Nothing printed from reception.");
        note.ConfirmStep(note.Steps[0].Id);
        await review.SaveAsync(note.ToSchema());
        await store.DisposeAsync();

        var reopened = new ReviewSession(await OpenAsync(), "s1");
        var reloaded = new NoteDraft((await reopened.LoadAsync())!.Draft!);

        Assert.Equal("Nothing printed from reception.", reloaded.Problem);
        Assert.False(reloaded.Steps[0].NeedsVerification);
    }

    [Fact]
    public async Task DiscardTakesEverythingAndLeavesTheRecordThatItDid()
    {
        var store = await OpenAsync();
        await SeedAsync(store);
        const string sessionId = "s1";
        var review = new ReviewSession(store, sessionId);

        await review.DiscardAsync();

        var after = (await review.LoadAsync())!;
        Assert.Null(after.Draft);
        Assert.Empty(after.Frames);
        Assert.Contains(await store.GetAuditAsync("s1"), entry => entry.Type == AuditTypes.SessionDiscarded);

        // The row is kept so Spec §5 S4 can filter history by Discarded, which is only true if the state
        // is actually written. Nothing else in the codebase reads that column yet, so without this the
        // claim in ISessionStore's own documentation is pinned by nothing.
        Assert.Equal([sessionId], await store.ListSessionsInStatesAsync([SessionStateNames.Discarded]));
    }

    [Fact]
    public void AReviewWithNoSessionIsRefusedAtConstruction()
    {
        // Every method here is addressed by id, and the discard is irreversible. An empty id would match no
        // row and quietly do nothing, which reads exactly like a discard that worked.
        Assert.Throws<ArgumentException>(() => new ReviewSession(new UnusedStore(), "  "));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private static async Task SeedAsync(SqliteSessionStore store)
    {
        await store.CreateSessionAsync(new NewSession(
            "s1",
            DateTimeOffset.UnixEpoch,
            new RemoteTool { Kind = RemoteToolKind.Screenconnect },
            LocalOnly: false,
            PolicyVersion: "v14"));
        await store.StageFrameAsync("s1", new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 0xAA }));
        await store.MarkFrameRedactedAsync("f1", new RedactionOutcome(new byte[] { 0xBB }, null, [], SensitiveContext: false, DateTimeOffset.UnixEpoch));
        await store.SaveDraftAsync("s1", Draft(Step("Found the spooler stopped.", StepConfidence.Low, "f1")));
    }

    private async Task<SqliteSessionStore> OpenAsync()
    {
        var store = await SqliteSessionStore.OpenAsync(_path, _key);
        _stores.Add(store);
        return store;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    /// <summary>Never touched: the constructor must refuse before it can reach a store at all.</summary>
    private sealed class UnusedStore : ISessionStore
    {
        public Task<IReadOnlyDictionary<string, long>> GetFrameImageSizesAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CreateSessionAsync(NewSession session, CancellationToken ct = default) => throw new NotSupportedException();

        public Task AppendEventAsync(string sessionId, SessionEvent sessionEvent, CancellationToken ct = default) => throw new NotSupportedException();

        public Task AppendTranscriptAsync(string sessionId, TranscriptSegment segment, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StageFrameAsync(string sessionId, StagedFrame frame, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PendingFrame?> TakeNextPendingFrameAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PendingFrame?> TakeNextPendingFrameAsync(IReadOnlySet<string>? except, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RecordPurgedFramesAsync(string sessionId, int count, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SaveRedactedFrameAsync(string sessionId, StagedFrame frame, RedactionOutcome outcome, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MarkFrameRedactedAsync(string frameId, RedactionOutcome outcome, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CountPendingFramesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CountAllPendingFramesAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task DiscardPendingFrameAsync(string frameId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PurgePendingFramesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetFrameExcludedAsync(string frameId, bool excluded, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SaveDraftAsync(string sessionId, DraftNote draft, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DraftNote?> LoadOriginalDraftAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task FinalizeSessionAsync(string sessionId, FinalizeInfo info, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetSessionStateAsync(string sessionId, string state, string? reason, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListSessionsInStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long> GetLastTimestampAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListSessionsWithRawDataOlderThanAsync(DateTimeOffset cutoff, string? activeSessionId = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PurgeRawDataAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> DiscardSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteFrameAsync(string frameId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ApplyUserBlurAsync(string frameId, ReadOnlyMemory<byte> image, MaskedRegion region, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<double> FreeSpaceFractionAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task VacuumAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<byte[]?> GetRedactedFrameImageAsync(string frameId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> GetSchemaVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
