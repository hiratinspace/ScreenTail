using System.Security.Cryptography;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Store;

/// <summary>ST-044 / INV-12: raw data expires, notes and audit stay, "delete everything" removes tokens too.</summary>
public sealed class RetentionTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 14, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedKeyProvider _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly ManualTime _time = new(T0);
    private SqliteSessionStore? _store;

    [Fact]
    public async Task ExpiredSessionLosesRawDataButKeepsNoteAndAudit()
    {
        var store = await OpenAsync();
        await FinishedSessionAsync(store, "old", frames: 40);
        var before = DiskBytes();
        var job = new RetentionJob(store, _time, new RetentionOptions());

        _time.Now = T0 + TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
        var purged = await job.RunAsync();

        Assert.Equal(1, purged);
        var loaded = (await store.LoadSessionAsync("old"))!;
        Assert.Empty(loaded.Frames);
        Assert.Empty(loaded.Transcript);
        Assert.Empty(loaded.Events);
        Assert.NotNull(loaded.Draft);
        Assert.Equal("Printer offline", loaded.Draft.SuggestedTitle);
        Assert.Empty(SessionValidator.Validate(loaded));
        Assert.Null(await store.GetRedactedFrameImageAsync("old-f0"));
        var audit = await store.GetAuditAsync("old");
        Assert.Contains(audit, a => a.Type == AuditTypes.RetentionPurged && a.Count == 40);
        Assert.Contains(audit, a => a.Type == AuditTypes.FramesPurgedUnredacted); // the pre-existing row survived
        Assert.True(DiskBytes() < before / 4, $"file went from {before} to {DiskBytes()} bytes; VACUUM should have reclaimed the frames");
    }

    [Fact]
    public async Task OneDayRetentionPurgesAfter25Hours()
    {
        var store = await OpenAsync();
        await FinishedSessionAsync(store, "s", frames: 2);
        var job = new RetentionJob(store, _time, new RetentionOptions { Retention = TimeSpan.FromDays(1) });

        _time.Now = T0 + TimeSpan.FromHours(23);
        Assert.Equal(0, await job.RunAsync());
        Assert.Equal(2, (await store.LoadSessionAsync("s"))!.Frames.Count);

        _time.Now = T0 + TimeSpan.FromHours(25);
        Assert.Equal(1, await job.RunAsync());

        var loaded = (await store.LoadSessionAsync("s"))!;
        Assert.Empty(loaded.Frames);
        Assert.NotNull(loaded.Draft);
    }

    [Fact]
    public async Task RecentSessionsAndTheOneBeingRecordedAreUntouched()
    {
        var store = await OpenAsync();
        await FinishedSessionAsync(store, "recent", frames: 1);
        await store.CreateSessionAsync(new NewSession("live", T0, Tool(), false, null));
        await store.StageFrameAsync("live", Frame("live-f", 1));
        var job = new RetentionJob(store, _time, new RetentionOptions(), () => "live");

        _time.Now = T0 + TimeSpan.FromDays(6);
        Assert.Equal(0, await job.RunAsync());

        // Even long past the cutoff, the session being recorded right now keeps its data.
        _time.Now = T0 + TimeSpan.FromDays(60);
        Assert.Equal(1, await job.RunAsync());
        Assert.Equal(1, await store.CountPendingFramesAsync("live"));
        Assert.Empty((await store.LoadSessionAsync("recent"))!.Frames);
    }

    [Fact]
    public async Task UnfinishedSessionStillAgesOut()
    {
        // A crash left this one recording and its recovery never ran. Without the COALESCE fallback its
        // raw frames would sit on disk for ever, which is exactly what INV-12 forbids.
        var store = await OpenAsync();
        await store.CreateSessionAsync(new NewSession("orphan", T0, Tool(), false, null));
        await store.StageFrameAsync("orphan", Frame("orphan-f", 1));
        await store.AppendTranscriptAsync("orphan", new TranscriptSegment { Id = "orphan-t", TsMs = 2, EndMs = 3, Speaker = Speaker.Tech, Text = "the password is hunter2" });
        var job = new RetentionJob(store, _time, new RetentionOptions(), () => null);

        _time.Now = T0 + TimeSpan.FromDays(8);
        Assert.Equal(1, await job.RunAsync());

        Assert.Equal(0, await store.CountPendingFramesAsync("orphan"));
        var loaded = (await store.LoadSessionAsync("orphan"))!;
        Assert.Empty(loaded.Frames);
        Assert.Empty(loaded.Transcript);
    }

    [Fact]
    public async Task RunningTwiceIsIdempotent()
    {
        var store = await OpenAsync();
        await FinishedSessionAsync(store, "s", frames: 1);
        var job = new RetentionJob(store, _time, new RetentionOptions());
        _time.Now = T0 + TimeSpan.FromDays(8);

        Assert.Equal(1, await job.RunAsync());
        Assert.Equal(0, await job.RunAsync());
        Assert.Single(await store.GetAuditAsync("s"), a => a.Type == AuditTypes.RetentionPurged);
    }

    [Fact]
    public async Task DeleteEverythingRemovesDataAndTokens()
    {
        var store = await OpenAsync();
        await FinishedSessionAsync(store, "s", frames: 1);
        await store.DisposeAsync();
        _store = null;
        File.WriteAllBytes(Path.Combine(_dir, "store.key"), new byte[64]);
        File.WriteAllBytes(Path.Combine(_dir, "ipc.token"), new byte[32]);
        Directory.CreateDirectory(Path.Combine(_dir, "tokens"));
        File.WriteAllText(Path.Combine(_dir, "tokens", "device.jwt"), "eyJ");
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        File.WriteAllText(Path.Combine(_dir, "models", "ggml-base.bin"), "model");

        var deleted = LocalDataEraser.Erase(_dir);

        Assert.False(File.Exists(Path.Combine(_dir, "store.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "store.key")));
        Assert.False(File.Exists(Path.Combine(_dir, "ipc.token")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "tokens")));
        Assert.True(File.Exists(Path.Combine(_dir, "models", "ggml-base.bin")), "models are not user data and stay");
        Assert.Contains(Path.Combine(_dir, "store.db"), deleted.Deleted);
        Assert.Contains(Path.Combine(_dir, "tokens"), deleted.Deleted);
        Assert.True(deleted.Complete);
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

    private async Task<SqliteSessionStore> OpenAsync()
    {
        Directory.CreateDirectory(_dir);
        return _store = await SqliteSessionStore.OpenAsync(Path.Combine(_dir, "store.db"), _key, _time);
    }

    /// <summary>A drafted, finished session with redacted frames, a transcript, events and one audit row, ended at the clock's current time.</summary>
    private static async Task FinishedSessionAsync(SqliteSessionStore store, string id, int frames)
    {
        await store.CreateSessionAsync(new NewSession(id, T0, Tool(), false, "v14"));
        var image = RandomNumberGenerator.GetBytes(150 * 1024);
        for (var i = 0; i < frames; i++)
        {
            await store.StageFrameAsync(id, Frame($"{id}-f{i}", i * 1000, image));
            await store.MarkFrameRedactedAsync($"{id}-f{i}", new RedactionOutcome(image, "Service status: Stopped", [], false, T0));
        }

        await store.AppendEventAsync(id, new ClickEvent { TsMs = 1, X = 1, Y = 1, Button = MouseButton.Left });
        await store.AppendTranscriptAsync(id, new TranscriptSegment { Id = $"{id}-t", TsMs = 2, EndMs = 3, Speaker = Speaker.Tech, Text = "the password is [REDACTED]" });
        await store.StageFrameAsync(id, Frame($"{id}-straggler", 9_000, image));
        await store.PurgePendingFramesAsync(id);
        await store.SaveDraftAsync(id, new DraftNote
        {
            Problem = "p",
            Steps = [new DraftStep { Text = "t", Confidence = StepConfidence.High, FrameRefs = [$"{id}-f0"] }],
            Result = "r",
            FollowUps = [],
            SuggestedTitle = "Printer offline",
            SuggestedTimeMinutes = 30,
            KbCandidate = false,
            KbReason = "n",
            Source = DraftSource.Cloud,
            PromptVersion = "note_v1",
        });
        await store.FinalizeSessionAsync(id, new FinalizeInfo(10_000, false));
        await store.SetSessionStateAsync(id, Shared.Ipc.CaptureStates.DraftReady, null);
    }

    private static readonly string[] DatabaseFiles = ["store.db", "store.db-wal"];

    private long DiskBytes() => DatabaseFiles
        .Select(n => Path.Combine(_dir, n))
        .Where(File.Exists)
        .Sum(p => new FileInfo(p).Length);

    private static RemoteTool Tool() => new() { Kind = RemoteToolKind.Rdp };

    private static StagedFrame Frame(string id, long tsMs, byte[]? image = null) =>
        new(id, tsMs, FrameTrigger.Click, 1600, 900, null, image ?? new byte[] { 1, 2, 3 });

    private sealed class FixedKeyProvider(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
