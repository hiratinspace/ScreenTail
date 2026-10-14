using System.Security.Cryptography;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Review;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The service's side of Review over the pipe (ST-085 remainder).
///
/// The UI has no store, and until 2026-09-25 nothing on the pipe could carry a session or a frame to it:
/// the Review views were fed only by the screenshot harness, and the shell's Review area showed the word
/// "Review". These are the questions and commands that make the pane real, answered against the real
/// encrypted store so that INV-1's read-path filtering is exercised, not assumed.
/// </summary>
public sealed class ReviewCommandsTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly RecordingMasker _masker = new();
    private SqliteSessionStore? _store;

    [Fact]
    public async Task TheSessionCrossesThePipeWithRedactedFramesOnly()
    {
        // The frame that is still pending must not be in what the UI receives. LoadSessionAsync filters
        // it; this proves the filter is on the path the pipe takes and not only on some other caller's.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1", "f2"], pending: ["f3"]);

        var reply = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" });

        var loaded = Assert.IsType<SessionLoaded>(reply);
        Assert.Equal(["f1", "f2"], loaded.Session.Frames.Select(f => f.Id).Order());
        Assert.All(loaded.Session.Frames, f => Assert.False(f.RedactionPending));
        Assert.NotNull(loaded.Session.Draft);
    }

    [Fact]
    public async Task ASessionThatDoesNotExistIsRefusedNotInvented()
    {
        var commands = await CommandsAsync();

        Assert.Null(await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "nope" }));
        var result = await commands.HandleAsync(new GetSessionCommand { RequestId = 1, SessionId = "nope" });

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Contains("session", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFrameComesBackAsItsRedactedBytes()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1"], pending: []);

        var reply = await commands.ReplyToAsync(new GetFrameCommand { RequestId = 2, FrameId = "f1" });

        var frame = Assert.IsType<FrameLoaded>(reply);
        Assert.Equal("f1", frame.FrameId);
        Assert.Equal(RedactedBytes("f1"), frame.Image);
    }

    [Fact]
    public async Task APendingFrameIsNotAFrame()
    {
        // INV-1 on the frame path: the pending image exists in the store and must never leave it.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: ["f3"]);

        Assert.Null(await commands.ReplyToAsync(new GetFrameCommand { RequestId = 2, FrameId = "f3" }));
        var result = await commands.HandleAsync(new GetFrameCommand { RequestId = 2, FrameId = "f3" });
        Assert.False(result!.Ok);
    }

    [Fact]
    public async Task AFrameTooBigForThePipeIsRefusedNotDropped()
    {
        // Writing an oversized message closes the connection, which the UI would see as the service
        // dying. A refusal with a reason is the honest answer, and the limit is named so the number is
        // one place.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync("s1", Staged("huge", 5000), new RedactionOutcome(new byte[ReviewCommands.LargestFrameBytes + 1], "t", [], false, DateTimeOffset.UnixEpoch));

        Assert.Null(await commands.ReplyToAsync(new GetFrameCommand { RequestId = 3, FrameId = "huge" }));
        var result = await commands.HandleAsync(new GetFrameCommand { RequestId = 3, FrameId = "huge" });

        Assert.False(result!.Ok);
        Assert.Contains("large", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABlurIsPaintedWrittenAndHandedBack()
    {
        // In that order. The store holds the new bytes before the UI is told anything, so a crash between
        // the two cannot leave a covered password on screen over an uncovered one on disk.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1"], pending: []);

        var reply = await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "f1", X = 10, Y = 20, Width = 300, Height = 40 });

        var frame = Assert.IsType<FrameLoaded>(reply);
        Assert.Equal(_masker.Painted, frame.Image);
        Assert.Equal(_masker.Painted, await _store!.GetRedactedFrameImageAsync("f1"));
        var region = Assert.Single(_masker.Regions);
        Assert.Equal((10L, 20L, 300L, 40L, MaskKind.UserBlur), (region.X, region.Y, region.Width, region.Height, region.Kind));
        var stored = (await _store.LoadSessionAsync("s1"))!.Frames.Single(f => f.Id == "f1");
        Assert.Contains(stored.MaskedRegions, r => r.Kind == MaskKind.UserBlur && r.X == 10 && r.Width == 300);
    }

    [Fact]
    public async Task ABlurOnAFrameThatIsGoneIsRefused()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);

        Assert.Null(await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "gone", X = 0, Y = 0, Width = 10, Height = 10 }));
        var result = await commands.HandleAsync(new BlurFrameCommand { RequestId = 4, FrameId = "gone", X = 0, Y = 0, Width = 10, Height = 10 });

        Assert.False(result!.Ok);
        Assert.Empty(_masker.Regions);
    }

    [Fact]
    public async Task ExcludingAndDeletingPersist()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1", "f2"], pending: []);

        var excluded = await commands.HandleAsync(new SetFrameIncludedCommand { RequestId = 5, FrameId = "f1", Included = false });
        var deleted = await commands.HandleAsync(new DeleteFrameCommand { RequestId = 6, FrameId = "f2" });
        var again = await commands.HandleAsync(new DeleteFrameCommand { RequestId = 7, FrameId = "f2" });

        Assert.True(excluded!.Ok);
        Assert.True(deleted!.Ok);
        Assert.False(again!.Ok);
        var session = (await _store!.LoadSessionAsync("s1"))!;
        Assert.True(session.Frames.Single(f => f.Id == "f1").ExcludedByUser);
        Assert.DoesNotContain(session.Frames, f => f.Id == "f2");
    }

    [Fact]
    public async Task TheDraftWrittenIsTheDraftReadBack()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        var edited = Draft() with { Problem = "Nothing printed from reception." };

        var saved = await commands.HandleAsync(new SaveDraftCommand { RequestId = 8, SessionId = "s1", Draft = edited });
        var reply = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 9, SessionId = "s1" });

        Assert.True(saved!.Ok);
        Assert.Equal("Nothing printed from reception.", Assert.IsType<SessionLoaded>(reply).Session.Draft!.Problem);
    }

    [Fact]
    public async Task ALongOcrDenseSessionFitsThePipeWithItsTextLeftBehind()
    {
        // E-20: roughly two hours of busy, text-heavy capture. With every frame's OCR text inside it the
        // reply passed the pipe's 1 MiB frame limit, the write threw, and the server dropped the UI's
        // connection: the session could never be opened in Review. The text now travels per frame, on
        // demand, so the session itself is a fraction of the limit.
        var commands = await CommandsAsync();
        await SeedLongAsync(frames: 600, ocrChars: 2_000, events: 4_000, segments: 800, segmentChars: 120);

        var reply = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" });

        // Exactly what IpcServer does with it: written as an IpcEvent, through the framing that refuses
        // anything over the limit.
        var loaded = Assert.IsType<SessionLoaded>(reply);
        using var wire = new MemoryStream();
        await IpcFraming.WriteAsync<IpcEvent>(wire, loaded with { RequestId = 1 });
        Assert.InRange(wire.Length, 1, IpcFraming.MaxMessageBytes);

        Assert.Equal(600, loaded.Session.Frames.Count);
        Assert.Equal(4_000, loaded.Session.Events.Count);
        Assert.Equal(800, loaded.Session.Transcript.Count);
        Assert.DoesNotContain(loaded.Session.Frames, frame => frame.OcrText is not null);
    }

    [Fact]
    public async Task ASessionTooLargeForThePipeEvenWithoutItsTextIsRefusedWithAReason()
    {
        // What is left is the timeline and the transcript, which the pane needs whole. A session whose
        // words alone pass the limit is refused with its size, never written to break the connection.
        var commands = await CommandsAsync();
        await SeedLongAsync(frames: 10, ocrChars: 10, events: 10, segments: 600, segmentChars: 2_000);

        Assert.Null(await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" }));
        var result = await commands.HandleAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" });

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Contains("too large", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(IpcFraming.MaxMessageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFramesTextComesBackOnItsOwn()
    {
        // What the session no longer carries, one frame at a time, for `T` in the enlarged view.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1"], pending: []);

        var reply = await commands.ReplyToAsync(new GetFrameTextCommand { RequestId = 2, FrameId = "f1" });

        var text = Assert.IsType<FrameTextLoaded>(reply);
        Assert.Equal("f1", text.FrameId);
        Assert.Equal("Service status: Stopped", text.OcrText);
        Assert.True((await commands.HandleAsync(new GetFrameTextCommand { RequestId = 2, FrameId = "f1" }))!.Ok);
    }

    [Fact]
    public async Task AFrameWithNoTextIsAnsweredWithNone()
    {
        // A redacted frame the OCR read nothing on is a frame, not a refusal.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync("s1", Staged("blank", 3000), new RedactionOutcome(RedactedBytes("blank"), null, [], false, DateTimeOffset.UnixEpoch));

        var reply = await commands.ReplyToAsync(new GetFrameTextCommand { RequestId = 2, FrameId = "blank" });

        Assert.Null(Assert.IsType<FrameTextLoaded>(reply).OcrText);
    }

    [Theory]
    [InlineData("f3")] // pending: its row exists and must not be read through this path (INV-1)
    [InlineData("nope")]
    public async Task APendingOrMissingFramesTextIsRefused(string frameId)
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: ["f3"]);

        Assert.Null(await commands.ReplyToAsync(new GetFrameTextCommand { RequestId = 2, FrameId = frameId }));
        var result = await commands.HandleAsync(new GetFrameTextCommand { RequestId = 2, FrameId = frameId });

        Assert.False(result!.Ok);
        Assert.Contains("no such frame", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFramesTextTooLargeForThePipeIsRefusedNotDropped()
    {
        // Measured in bytes on the wire, not characters: each of these is six bytes once escaped, so a
        // text well under a million characters is still over the limit.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        var text = new string('é', 200_000);
        await _store!.SaveRedactedFrameAsync("s1", Staged("dense", 3000), new RedactionOutcome(RedactedBytes("dense"), text, [], false, DateTimeOffset.UnixEpoch));

        Assert.Null(await commands.ReplyToAsync(new GetFrameTextCommand { RequestId = 2, FrameId = "dense" }));
        var result = await commands.HandleAsync(new GetFrameTextCommand { RequestId = 2, FrameId = "dense" });

        Assert.False(result!.Ok);
        Assert.Contains("too large", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSessionLeavesTheStoreUntouched()
    {
        // The text is left off the reply, not out of the store: the bundle and the drafter still read it.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1"], pending: []);

        var reply = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" });

        Assert.Null(Assert.Single(Assert.IsType<SessionLoaded>(reply).Session.Frames).OcrText);
        Assert.Equal("Service status: Stopped", Assert.Single((await _store!.LoadSessionAsync("s1"))!.Frames).OcrText);
    }

    [Fact]
    public async Task ASessionAtTheLimitIsSentAndOneByteOverIsRefused()
    {
        // The boundary is the framing's own: a message of exactly MaxMessageBytes is written, one more
        // byte is not. A transcript segment's text is grown one character at a time across it.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        var empty = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" });
        var segment = new TranscriptSegment { Id = "t1", TsMs = 1, EndMs = 2, Speaker = Speaker.Tech, Text = "x" };
        var withOne = IpcFraming.MeasureBytes<IpcEvent>(Assert.IsType<SessionLoaded>(empty) with { Session = ((SessionLoaded)empty!).Session with { Transcript = [segment] } });
        var fill = IpcFraming.MaxMessageBytes - withOne + 1;
        await _store!.AppendTranscriptAsync("s1", segment with { Text = new string('x', (int)fill) });

        using var wire = new MemoryStream();
        var atLimit = Assert.IsType<SessionLoaded>(await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" }));
        await IpcFraming.WriteAsync<IpcEvent>(wire, atLimit);
        Assert.Equal(IpcFraming.MaxMessageBytes + 4, wire.Length);

        await _store.SaveDraftAsync("s1", Draft() with { Problem = Draft().Problem + "!" });
        Assert.Null(await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" }));
        Assert.False((await commands.HandleAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" }))!.Ok);
    }

    [Fact]
    public async Task CommandsItDoesNotOwnAreLeftForTheController()
    {
        // Null, not a failure: the controller chains this before its own switch, and a "not mine" that
        // read as "refused" would turn every start command into a refusal.
        var commands = await CommandsAsync();

        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 1 }));
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 1 }));
    }

    private async Task<ReviewCommands> CommandsAsync()
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        return new ReviewCommands(_store, _masker);
    }

    private async Task SeedAsync(string[] redacted, string[] pending)
    {
        var store = _store!;
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        foreach (var id in redacted)
        {
            await store.SaveRedactedFrameAsync("s1", Staged(id, 1000), new RedactionOutcome(RedactedBytes(id), "Service status: Stopped", [], false, DateTimeOffset.UnixEpoch));
        }

        foreach (var id in pending)
        {
            await store.StageFrameAsync("s1", Staged(id, 2000));
        }

        await store.SaveDraftAsync("s1", Draft());
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));
    }

    /// <summary>
    /// One session with this much in it. Ids are the shape the capture service writes (a GUID per frame
    /// and segment) so the sizes measured are the sizes a real session would have.
    /// </summary>
    private async Task SeedLongAsync(int frames, int ocrChars, int events, int segments, int segmentChars)
    {
        var store = _store!;
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        var ocr = new string('x', ocrChars);
        var regions = new List<MaskedRegion> { new() { X = 10, Y = 20, Width = 300, Height = 40, Kind = MaskKind.Email } };
        for (var i = 0; i < frames; i++)
        {
            var staged = new StagedFrame(Guid.NewGuid().ToString(), i * 1_000L, FrameTrigger.Click, 1600, 900, new Point { X = 800, Y = 450 }, new byte[] { 1 });
            await store.SaveRedactedFrameAsync("s1", staged, new RedactionOutcome(new byte[] { 2 }, ocr, regions, false, DateTimeOffset.UnixEpoch));
        }

        for (var i = 0; i < events; i++)
        {
            await store.AppendEventAsync("s1", new ClickEvent { TsMs = i * 500L, X = 1_234, Y = 567, Button = MouseButton.Left });
        }

        var words = new string('w', segmentChars);
        for (var i = 0; i < segments; i++)
        {
            await store.AppendTranscriptAsync("s1", new TranscriptSegment { Id = Guid.NewGuid().ToString(), TsMs = i * 5_000L, EndMs = (i * 5_000L) + 4_000, Speaker = Speaker.Tech, Text = words, Confidence = 0.9 });
        }

        await store.SaveDraftAsync("s1", Draft());
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(frames * 1_000L, false));
    }

    private static StagedFrame Staged(string id, long ts) => new(id, ts, FrameTrigger.Click, 1600, 900, null, new byte[] { 1, 2, 3 });

    private static byte[] RedactedBytes(string id) => [.. System.Text.Encoding.ASCII.GetBytes("redacted-" + id)];

    /// <summary>Paints nothing; remembers what it was asked to paint and hands back bytes of its own.</summary>
    private sealed class RecordingMasker : IFrameMasker
    {
        public byte[] Painted { get; } = [0xCC, 0xCC, 0xCC];

        public List<MaskedRegion> Regions { get; } = [];

        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge)
        {
            Regions.AddRange(regions);
            return new MaskedImage(Painted, 1600, 900);
        }
    }

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

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }
}
