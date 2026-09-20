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
    public async Task TheSameTranscriptIdInTwoSessionsIsTwoSegments()
    {
        // 2026-09-19 review. Transcript ids are "t-0001" upwards, counted from when the capture service
        // started, and the id was a table-wide primary key. So the first thing a technician said after
        // any service restart collided with a row from an earlier session that retention had not yet
        // removed. The insert failed, the exception left the narration loop — which caught only
        // cancellation — and narration stopped silently for the life of the service.
        //
        // Every session after the first restart was recorded without a word of what was said.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("yesterday"));
        await store.CreateSessionAsync(Session("today"));

        await store.AppendTranscriptAsync("yesterday", Spoken("t-0001", "the spooler was stopped"));
        await store.AppendTranscriptAsync("today", Spoken("t-0001", "the printer is offline again"));

        Assert.Equal(
            "the spooler was stopped",
            Assert.Single((await store.LoadSessionAsync("yesterday"))!.Transcript).Text);
        Assert.Equal(
            "the printer is offline again",
            Assert.Single((await store.LoadSessionAsync("today"))!.Transcript).Text);
    }

    [Fact]
    public async Task TheSameTranscriptIdTwiceInOneSessionIsStillRefused()
    {
        // The half the composite key must keep: within a session an id names one segment, because the
        // drafting prompt cites them and a citation has to mean something.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.AppendTranscriptAsync("s1", Spoken("t-0001", "first"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.AppendTranscriptAsync("s1", Spoken("t-0001", "second")));
    }

    [Fact]
    public async Task MigrationsApplyOnce()
    {
        var latest = SqliteSessionStore.LatestSchemaVersion;
        Assert.True(latest >= 2, "expected at least the initial and session-state migrations");
        var first = await OpenAsync();
        Assert.Equal(latest, await first.GetSchemaVersionAsync());
        await first.DisposeAsync();

        var second = await OpenAsync();
        Assert.Equal(latest, await second.GetSchemaVersionAsync());
    }

    private static TranscriptSegment Spoken(string id, string text) => new()
    {
        Id = id,
        TsMs = 1_000,
        EndMs = 2_000,
        Speaker = Speaker.Tech,
        Text = text,
    };

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

    [Fact]
    public async Task DiscardingFromReviewTakesTheNoteWithTheEvidence()
    {
        // ST-074's Discard. PurgeRawDataAsync keeps the drafted note on purpose - retention ages out the
        // evidence and leaves the deliverable - and here the note is the thing being thrown away. A discard
        // that left a readable note behind would be the worst of both: the technician believes it is gone.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1000, Bytes(0xAA, 2000)));
        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(0xBB, 1500), "Service status: Stopped"));
        await store.AppendEventAsync("s1", new ClickEvent { TsMs = 990, X = 1, Y = 1, Button = MouseButton.Left });
        await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = "t1", TsMs = 995, EndMs = 1200, Speaker = Speaker.Tech, Text = "hi" });
        await store.SaveDraftAsync("s1", Note());

        Assert.Equal(1, await store.DiscardSessionAsync("s1"));

        // The row survives, because Spec §5 S4 lets a technician filter history by Discarded and a row that
        // is gone cannot have a status.
        var loaded = (await store.LoadSessionAsync("s1"))!;
        Assert.Empty(loaded.Frames);
        Assert.Empty(loaded.Events);
        Assert.Empty(loaded.Transcript);
        Assert.Null(loaded.Draft);
        Assert.Null(await store.GetRedactedFrameImageAsync("f1"));

        // And it says so: INV-12 keeps the audit log through every deletion, so there is a record that a
        // human threw this away rather than a silent gap in the history.
        var audit = await store.GetAuditAsync("s1");
        var row = Assert.Single(audit, entry => entry.Type == AuditTypes.SessionDiscarded);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task DiscardingASessionThatIsNotThereWritesNothing()
    {
        var store = await OpenAsync();

        Assert.Equal(0, await store.DiscardSessionAsync("never-existed"));
        Assert.Empty(await store.GetAuditAsync("never-existed"));
    }

    private static DraftNote Note() => new()
    {
        Problem = "Nothing printed.",
        Steps = [new DraftStep { Text = "Started the spooler.", Confidence = StepConfidence.High, FrameRefs = [] }],
        Result = "It prints.",
        FollowUps = [],
        SuggestedTitle = "Printer offline",
        SuggestedTimeMinutes = 30,
        KbCandidate = false,
        KbReason = "one-off",
        Source = DraftSource.Cloud,
        PromptVersion = "note_v1",
    };

    [Fact]
    public async Task BlurringAFrameLeavesTheStoreHoldingOnlyTheBlurredImage()
    {
        // ST-075's first AC. The first version of this test searched the database file for the original
        // bytes - which cannot fail: the store is SQLCipher, so no plaintext image appears in the file
        // whatever the code does. Removing the UPDATE's image column entirely still passed it. What can be
        // checked is that the store has one image per frame, it is the blurred one, and it reads that way
        // through a connection that was not open when the blur happened.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1000, Bytes(0xAA, 2000)));
        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(0xBB, 1500), "Password: hunter2"));

        var blurred = Bytes(0xCC, 1400);
        await store.ApplyUserBlurAsync("f1", blurred, new MaskedRegion { X = 40, Y = 12, Width = 120, Height = 20, Kind = MaskKind.UserBlur });
        await store.DisposeAsync();

        var reopened = await OpenAsync();
        Assert.Equal(blurred, await reopened.GetRedactedFrameImageAsync("f1"));
        Assert.Single((await reopened.LoadSessionAsync("s1"))!.Frames);

        // The original is unrecoverable because nothing kept it: the undo window holds it in memory alone
        // (UndoWindowTests), and ApplyUserBlurAsync overwrites the one column that had it. There is no
        // history table for this test to check, and that absence is the guarantee.
    }

    [Fact]
    public async Task BlurringRecordsTheRegionAlongsideWhatRedactionAlreadyFound()
    {
        // The regions are how Review shows what was hidden and by whom. Overwriting the redaction worker's
        // findings with the technician's one region would erase the record that a card number was masked.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1000, Bytes(0xAA, 2000)));
        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(0xBB, 1500), null));

        await store.ApplyUserBlurAsync("f1", Bytes(0xCC, 1400), new MaskedRegion { X = 40, Y = 12, Width = 120, Height = 20, Kind = MaskKind.UserBlur });

        var frame = Assert.Single((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.Equal([MaskKind.Card, MaskKind.UserBlur], frame.MaskedRegions.Select(region => region.Kind));
    }

    [Fact]
    public async Task AFrameStillWaitingForRedactionCannotBeBlurred()
    {
        // There is nothing readable to blur, and the caller is working from a strip that cannot have shown
        // it - LoadSessionAsync does not return pending frames (INV-1). Refusing loudly beats writing the
        // caller's bytes over a frame the redaction worker is about to overwrite.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1000, Bytes(0xAA, 2000)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ApplyUserBlurAsync("f1", Bytes(0xCC, 10), new MaskedRegion { X = 1, Y = 1, Width = 2, Height = 2, Kind = MaskKind.UserBlur }));
    }

    [Fact]
    public async Task DeletingAFrameSaysWhoDeletedItAndWhichSession()
    {
        // ST-075's third AC. The audit row is written in the same transaction as the delete, and its
        // session id is read before the row is gone - afterwards there is nothing left to read it from,
        // and an audit entry with no session cannot be found again by the person it concerns.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));
        await store.StageFrameAsync("s1", Staged("f1", 1000, Bytes(0xAA, 2000)));
        await store.MarkFrameRedactedAsync("f1", Outcome(Bytes(0xBB, 1500), null));

        Assert.True(await store.DeleteFrameAsync("f1"));

        Assert.Empty((await store.LoadSessionAsync("s1"))!.Frames);
        Assert.Null(await store.GetRedactedFrameImageAsync("f1"));
        var row = Assert.Single(await store.GetAuditAsync("s1"), entry => entry.Type == AuditTypes.FrameDeletedByUser);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task DeletingAFrameThatIsNotThereWritesNoAuditRow()
    {
        // An audit log that records deletions which never happened is an audit log nobody can reason from.
        var store = await OpenAsync();
        await store.CreateSessionAsync(Session("s1"));

        Assert.False(await store.DeleteFrameAsync("never-existed"));
        Assert.Empty(await store.GetAuditAsync("s1"));
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
