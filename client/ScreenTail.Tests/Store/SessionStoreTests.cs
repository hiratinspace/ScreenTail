using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Store;

/// <summary>ST-005: the encrypted store and the two-stage frame lifecycle (INV-1), on any OS.</summary>
public sealed class SessionStoreTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 10, 14, 2, 0, TimeSpan.FromHours(-5));
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKeyProvider _key = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SqliteSessionStore> _stores = [];

    [Fact]
    public async Task PendingFramesNeverReadable()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1000, Bytes(0xAA, 2000)));
        await store.AppendEventAsync("s1", new ClickEvent { TsMs = 990, X = 1, Y = 1, Button = MouseButton.Left, FrameId = "f1" });
        await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = "t1", TsMs = 995, EndMs = 1200, Speaker = Speaker.Tech, Text = "hi", FrameId = "f1" });

        // Every reader-facing path: no frame, no image, no reference to it.
        var loaded = (await store.LoadSessionAsync("s1"))!;
        Assert.Empty(loaded.Frames);
        Assert.Null(await store.GetRedactedFrameImageAsync("f1"));
        Assert.Null(Assert.IsType<ClickEvent>(loaded.Events[0]).FrameId);
        Assert.Null(loaded.Transcript[0].FrameId);
        Assert.Equal(1, await store.CountPendingFramesAsync("s1"));

        // The redaction worker is the one path that sees it.
        var pending = await store.TakeNextPendingFrameAsync();
        Assert.Equal("f1", pending!.Id);
        Assert.Equal(Bytes(0xAA, 2000), pending.Image);

        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(0xBB, 1500), "Service status: Stopped"));

        loaded = (await store.LoadSessionAsync("s1"))!;
        var frame = Assert.Single(loaded.Frames);
        Assert.False(frame.RedactionPending);
        Assert.Equal("Service status: Stopped", frame.OcrText);
        Assert.Single(frame.MaskedRegions);
        Assert.Equal(Bytes(0xBB, 1500), await store.GetRedactedFrameImageAsync("f1"));
        Assert.Equal("f1", Assert.IsType<ClickEvent>(loaded.Events[0]).FrameId);
        Assert.Equal("f1", loaded.Transcript[0].FrameId);
        Assert.Null(await store.TakeNextPendingFrameAsync());
    }

    [Fact]
    public async Task RedactingTwiceIsRefused()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1, Bytes(1, 10)));
        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(2, 10), null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkFrameRedactedAsync("f1", Outcome(Bytes(3, 10), null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkFrameRedactedAsync("missing", Outcome(Bytes(3, 10), null)));
    }

    [Fact]
    public async Task DatabaseOnDiskIsCiphertext()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        var marker = Encoding.UTF8.GetBytes("the admin password is Winter2026");
        await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = "t1", TsMs = 1, EndMs = 2, Speaker = Speaker.Tech, Text = Encoding.UTF8.GetString(marker) });
        await store.StageFrameAsync("s1", Staged("f1", 3, Enumerable.Repeat(marker, 40).SelectMany(b => b).ToArray()));
        await store.DisposeAsync();

        var raw = await File.ReadAllBytesAsync(_path);

        Assert.False(raw.AsSpan().StartsWith("SQLite format 3\0"u8), "plaintext SQLite header");
        Assert.Equal(-1, raw.AsSpan().IndexOf(marker));
    }

    [Fact]
    public async Task DifferentKeyCannotOpen()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.DisposeAsync();

        var other = new FixedKeyProvider(RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<StoreKeyException>(async () => await SqliteSessionStore.OpenAsync(_path, other));

        // The right key still works afterwards: a failed open must not damage the file.
        var reopened = await OpenAsync();
        Assert.NotNull(await reopened.LoadSessionAsync("s1"));
    }

    [Fact]
    public async Task ShortKeyIsRefused()
    {
        await Assert.ThrowsAsync<StoreKeyException>(async () => await SqliteSessionStore.OpenAsync(_path, new FixedKeyProvider(new byte[16])));
    }

    [Fact]
    public async Task PurgeDeletesStragglersAndRecordsIt()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("keep", 1, Bytes(1, 10)));
        await store.MarkFrameRedactedAsync("keep", Outcome(Bytes(1, 10), null));
        foreach (var id in new[] { "p1", "p2", "p3" })
        {
            await store.StageFrameAsync("s1", Staged(id, 2, Bytes(9, 10)));
        }

        var purged = await store.PurgePendingFramesAsync("s1");

        Assert.Equal(3, purged);
        Assert.Equal(0, await store.CountPendingFramesAsync("s1"));
        var loaded = (await store.LoadSessionAsync("s1"))!;
        Assert.Equal(3, loaded.FramesPurgedUnredacted);
        Assert.Equal("keep", Assert.Single(loaded.Frames).Id);
        var audit = Assert.Single(await store.GetAuditAsync("s1"));
        Assert.Equal(AuditTypes.FramesPurgedUnredacted, audit.Type);
        Assert.Equal(3, audit.Count);

        Assert.Equal(0, await store.PurgePendingFramesAsync("s1"));
        Assert.Single(await store.GetAuditAsync("s1"));
    }

    [Fact]
    public async Task LoadedSessionIsAValidDocumentEvenAfterPurge()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("gone", 10, Bytes(1, 10)));
        await store.AppendEventAsync("s1", new MarkerEvent { TsMs = 10, FrameId = "gone" });
        await store.SaveDraftAsync("s1", new DraftNote
        {
            Problem = "p",
            Steps = [new DraftStep { Text = "t", Confidence = StepConfidence.Low, FrameRefs = ["gone"], TranscriptRefs = ["nope"] }],
            Result = "r",
            FollowUps = [],
            SuggestedTitle = "s",
            SuggestedTimeMinutes = 15,
            KbCandidate = false,
            KbReason = "n",
            Source = DraftSource.Cloud,
            PromptVersion = "note_v1",
        });
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(60_000, PartialCapture: true));
        await store.PurgePendingFramesAsync("s1");

        var loaded = (await store.LoadSessionAsync("s1"))!;

        Assert.Empty(SessionValidator.Validate(loaded));
        Assert.Null(Assert.IsType<MarkerEvent>(loaded.Events[0]).FrameId);
        Assert.Empty(loaded.Draft!.Steps[0].FrameRefs);
        Assert.Empty(loaded.Draft.Steps[0].TranscriptRefs!);
        Assert.True(loaded.PartialCapture);
        Assert.Equal(60_000, loaded.DurationMs);
        Assert.NotEmpty(SessionJson.Serialize(loaded));
    }

    [Fact]
    public async Task ExcludedFlagRoundTrips()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1, Bytes(1, 10)));
        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(1, 10), null));

        await store.SetFrameExcludedAsync("f1", true);

        Assert.True((await store.LoadSessionAsync("s1"))!.Frames[0].ExcludedByUser);
    }

    [Fact]
    public async Task DeleteRemovesEverythingAndAudits()
    {
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1, Bytes(1, 10)));
        await store.AppendEventAsync("s1", new EnterEvent { TsMs = 1 });

        await store.DeleteSessionAsync("s1");

        Assert.Null(await store.LoadSessionAsync("s1"));
        Assert.Null(await store.TakeNextPendingFrameAsync());
        Assert.Equal(AuditTypes.SessionDiscarded, Assert.Single(await store.GetAuditAsync("s1")).Type);
    }

    [Fact]
    public async Task MigrationsApplyOnce()
    {
        // Bump when a migration is added: 0001 initial, 0002 session state (ST-020).
        const int latest = 2;
        var first = await OpenAsync();
        Assert.Equal(latest, await first.GetSchemaVersionAsync());
        await first.DisposeAsync();

        var second = await OpenAsync();
        Assert.Equal(latest, await second.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task FrameWritesKeepUpWithCapture()
    {
        // Budget: >= 10 frame writes/s without blocking capture. 60 x 150 KB in well under 6 s.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        var image = RandomNumberGenerator.GetBytes(150 * 1024);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
        {
            await store.StageFrameAsync("s1", Staged($"f{i}", i * 100, image));
        }

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"60 frames took {clock.Elapsed.TotalSeconds:0.00} s");
    }

    [Fact]
    public async Task TwentyMinuteSessionFitsTheDiskBudget()
    {
        // A busy 20-minute session: 25 click frames + 60 scene frames (ST-026's cap) at ~150 KB each,
        // 600 events, 120 transcript segments, all redacted. Budget: < 40 MB on disk.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        var image = RandomNumberGenerator.GetBytes(150 * 1024);
        for (var i = 0; i < 85; i++)
        {
            await store.StageFrameAsync("s1", Staged($"f{i}", i * 14_000, image));
            await store.MarkFrameRedactedAsync($"f{i}", Outcome(image, new string('x', 600)));
        }

        for (var i = 0; i < 600; i++)
        {
            await store.AppendEventAsync("s1", new ClickEvent { TsMs = i * 2000, X = i, Y = i, Button = MouseButton.Left });
        }

        for (var i = 0; i < 120; i++)
        {
            await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = $"t{i}", TsMs = i * 10_000, EndMs = i * 10_000 + 4000, Speaker = Speaker.Tech, Text = new string('w', 80) });
        }

        await store.DisposeAsync();

        var bytes = new FileInfo(_path).Length + (File.Exists(_path + "-wal") ? new FileInfo(_path + "-wal").Length : 0);
        Assert.True(bytes < 40L * 1024 * 1024, $"session used {bytes / (1024.0 * 1024):0.0} MB");
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

    private async Task<SqliteSessionStore> OpenAsync()
    {
        var store = await SqliteSessionStore.OpenAsync(_path, _key);
        _stores.Add(store);
        return store;
    }

    private static NewSession Session(string id) => new(id, At, new RemoteTool { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" }, LocalOnly: false, PolicyVersion: "v14");

    private static StagedFrame Staged(string id, long tsMs, byte[] image) => new(id, tsMs, FrameTrigger.Click, 1600, 900, new Point { X = 10, Y = 20 }, image);

    private static RedactionOutcome Outcome(byte[] image, string? ocr) => new(
        image,
        ocr,
        [new MaskedRegion { X = 1, Y = 2, Width = 30, Height = 10, Kind = MaskKind.Card }],
        SensitiveContext: false,
        At);

    private static byte[] Bytes(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    private sealed class FixedKeyProvider(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
