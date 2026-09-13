using System.Security.Cryptography;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// The redaction worker under things going wrong, found by an adversarial review of ST-041.
///
/// The bug these exist for was not a redaction bug: a frame whose store write threw took the worker's
/// whole loop with it, both workers went the same way, and from then on every staged frame stayed pending
/// until finalize purged it. A service that produced no screenshots at all for the rest of the day, with
/// nothing in the log to say why. It failed safe for INV-1 and destroyed the product.
/// </summary>
public sealed class RedactionResilienceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));
    private SqliteSessionStore? _store;

    [Fact]
    public async Task TwoWorkersNeverTakeTheSameFrame()
    {
        // A frame stays redaction_pending until its redaction finishes, so the query cannot tell "waiting"
        // from "being worked on". Both workers would take the same row, both would run OCR on it, and the
        // slower one's write would find nothing to update.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        var worker = Worker(store);

        var taken = await Task.WhenAll(
            worker.ProcessOneAsync(TestContext.Current.CancellationToken),
            worker.ProcessOneAsync(TestContext.Current.CancellationToken));

        // One of the two did the work; the other found nothing to do and said so.
        Assert.Contains(false, taken);
        Assert.Equal(1, worker.Progress.Frames);
        Assert.Equal(0, worker.Progress.Failed);
    }

    [Fact]
    public async Task AFrameThatCannotBeWrittenDoesNotStopTheQueue()
    {
        // The failure that killed redaction for the rest of the service run. The next frame has to be
        // tried, and the failure has to be counted rather than swallowed.
        var store = await OpenAsync();
        await StageAsync(store, "s1", "f1");
        await StageAsync(store, "s1", "f2");
        await StageAsync(store, "s1", "f3");
        var worker = new RedactionWorker(
            new RefusesOneWrite(store, "f2"), Reads(), new PassThroughMasker(), new RedactionEngine());

        using var stopping = new CancellationTokenSource();
        var running = worker.RunAsync(stopping.Token);
        await WaitUntilAsync(() => worker.Progress.Frames >= 2 && worker.Progress.Failed >= 1);
        await stopping.CancelAsync();
        await running;

        // The two good frames went through; the bad one was given up on rather than retried forever, and
        // is left pending so finalize purges it — a frame nobody could make readable must not be kept.
        Assert.Equal(2, worker.Progress.Frames);
        Assert.Equal(1, worker.Progress.Failed);
        Assert.Equal(1, await store.CountAllPendingFramesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheQueueStillDrainsWithBothWorkersRunning()
    {
        // The real configuration: concurrency 2 against a backlog. Before the claim, this produced a
        // double take on almost every frame and killed both loops within a few frames.
        var store = await OpenAsync();
        for (var i = 0; i < 25; i++)
        {
            await StageAsync(store, "s1", $"f{i:D2}");
        }

        var worker = Worker(store);
        using var stopping = new CancellationTokenSource();
        var running = worker.RunAsync(stopping.Token);
        await WaitUntilAsync(async () => await store.CountAllPendingFramesAsync() == 0);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(25, worker.Progress.Frames);
        Assert.Equal(0, worker.Progress.Failed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private async Task<SqliteSessionStore> OpenAsync() => _store ??= await SqliteSessionStore.OpenAsync(_path, _key);

    private static RedactionWorker Worker(ISessionStore store) =>
        new(store, Reads(), new PassThroughMasker(), new RedactionEngine());

    private static FixedText Reads() => new();

    private static async Task StageAsync(SqliteSessionStore store, string sessionId, string frameId)
    {
        if (await store.LoadSessionAsync(sessionId) is null)
        {
            await store.CreateSessionAsync(new NewSession(sessionId, At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        }

        await store.StageFrameAsync(sessionId, new StagedFrame(frameId, 1_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1, 2, 3 }));
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private sealed class FixedText : IFrameTextRecogniser
    {
        public Task<RecognisedText> ReadAsync(ReadOnlyMemory<byte> image, CancellationToken ct = default) =>
            Task.FromResult(new RecognisedText([new OcrWord("Services", 0, 0, 60, 12)], 0.9));
    }

    private sealed class PassThroughMasker : IFrameMasker
    {
        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) =>
            new(image.ToArray(), 80, 60);
    }

    /// <summary>Stands in for the store refusing one frame's write, the way a double take used to.</summary>
    private sealed class RefusesOneWrite(SqliteSessionStore inner, string frameId) : ISessionStore
    {
        public Task MarkFrameRedactedAsync(string id, RedactionOutcome outcome, CancellationToken ct = default) =>
            id == frameId
                ? throw new InvalidOperationException($"Frame '{id}' is not pending redaction.")
                : inner.MarkFrameRedactedAsync(id, outcome, ct);

        public Task<PendingFrame?> TakeNextPendingFrameAsync(CancellationToken ct = default) =>
            inner.TakeNextPendingFrameAsync(ct);

        public Task<PendingFrame?> TakeNextPendingFrameAsync(IReadOnlySet<string>? except, CancellationToken ct = default) =>
            inner.TakeNextPendingFrameAsync(except, ct);

        public Task DiscardPendingFrameAsync(string id, CancellationToken ct = default) => inner.DiscardPendingFrameAsync(id, ct);

        public Task<int> CountAllPendingFramesAsync(CancellationToken ct = default) => inner.CountAllPendingFramesAsync(ct);

        public Task<int> CountPendingFramesAsync(string sessionId, CancellationToken ct = default) => inner.CountPendingFramesAsync(sessionId, ct);

        public Task CreateSessionAsync(NewSession session, CancellationToken ct = default) => inner.CreateSessionAsync(session, ct);

        public Task AppendEventAsync(string sessionId, SessionEvent e, CancellationToken ct = default) => inner.AppendEventAsync(sessionId, e, ct);

        public Task AppendTranscriptAsync(string sessionId, TranscriptSegment segment, CancellationToken ct = default) => inner.AppendTranscriptAsync(sessionId, segment, ct);

        public Task StageFrameAsync(string sessionId, StagedFrame frame, CancellationToken ct = default) => inner.StageFrameAsync(sessionId, frame, ct);

        public Task<int> PurgePendingFramesAsync(string sessionId, CancellationToken ct = default) => inner.PurgePendingFramesAsync(sessionId, ct);

        public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(string? sessionId = null, CancellationToken ct = default) => inner.GetAuditAsync(sessionId, ct);

        public Task SetFrameExcludedAsync(string id, bool excluded, CancellationToken ct = default) => inner.SetFrameExcludedAsync(id, excluded, ct);

        public Task SaveDraftAsync(string sessionId, DraftNote draft, CancellationToken ct = default) => inner.SaveDraftAsync(sessionId, draft, ct);

        public Task FinalizeSessionAsync(string sessionId, FinalizeInfo info, CancellationToken ct = default) => inner.FinalizeSessionAsync(sessionId, info, ct);

        public Task SetSessionStateAsync(string sessionId, string state, string? reason, CancellationToken ct = default) => inner.SetSessionStateAsync(sessionId, state, reason, ct);

        public Task<IReadOnlyList<string>> ListSessionsInStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default) => inner.ListSessionsInStatesAsync(states, ct);

        public Task<long> GetLastTimestampAsync(string sessionId, CancellationToken ct = default) => inner.GetLastTimestampAsync(sessionId, ct);

        public Task<IReadOnlyList<string>> ListSessionsWithRawDataOlderThanAsync(DateTimeOffset cutoff, string? activeSessionId = null, CancellationToken ct = default) => inner.ListSessionsWithRawDataOlderThanAsync(cutoff, activeSessionId, ct);

        public Task<int> PurgeRawDataAsync(string sessionId, CancellationToken ct = default) => inner.PurgeRawDataAsync(sessionId, ct);

        public Task VacuumAsync(CancellationToken ct = default) => inner.VacuumAsync(ct);

        public Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default) => inner.LoadSessionAsync(sessionId, ct);

        public Task<byte[]?> GetRedactedFrameImageAsync(string frameId, CancellationToken ct = default) => inner.GetRedactedFrameImageAsync(frameId, ct);

        public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default) => inner.DeleteSessionAsync(sessionId, ct);

        public Task<int> GetSchemaVersionAsync(CancellationToken ct = default) => inner.GetSchemaVersionAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
