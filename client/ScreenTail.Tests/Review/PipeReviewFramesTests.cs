using System.IO.Pipes;
using System.Security.Cryptography;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The UI's side of Review over the pipe, end to end: a real server over a real store, a real client, and
/// the <see cref="PipeReviewFrames"/> the filmstrip and the note editor are handed. What this proves that
/// <see cref="ReviewCommandsTests"/> cannot is the wire: a session and a frame's bytes survive the JSON,
/// and a refusal on the service side is a null on this side rather than a thrown-away window.
/// </summary>
public sealed class PipeReviewFramesTests : IAsyncDisposable
{
    private readonly string _pipeName = IpcPipeNames.ForUser("test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _token = IpcToken.Generate();
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;
    private IpcServer? _server;
    private CaptureConnection? _connection;

    [Fact]
    public async Task TheSessionAndItsBytesArriveAsTheStoreHoldsThem()
    {
        var frames = await FramesAsync();
        var session = await frames.LoadSessionAsync("s1");

        Assert.NotNull(session);
        Assert.Equal(["f1"], session.Frames.Select(f => f.Id));
        Assert.Equal("The print spooler was stopped.", session.Draft!.Problem);
        Assert.Equal(RedactedBytes, await frames.ImageAsync(session.Frames[0]));
    }

    [Fact]
    public async Task ABlurComesBackAsTheNewImageAndTheStoreAgrees()
    {
        var frames = await FramesAsync();
        var region = new MaskedRegion { X = 5, Y = 6, Width = 70, Height = 8, Kind = MaskKind.UserBlur };

        var blurred = await frames.BlurAsync("f1", region);

        // A null here is a refusal the pane never sees the words of (CaptureConnection turns it into
        // null on purpose). It failed once on the hosted Windows runner (#165) and passed on rerun
        // and locally; the next time, the service's own reason is in the failure rather than "null".
        if (blurred is null)
        {
            var refusal = await _connection!.SendAsync(id => new BlurFrameCommand { RequestId = id, FrameId = "f1", X = region.X, Y = region.Y, Width = region.Width, Height = region.Height });
            Assert.Fail($"The blur came back null; asked again plainly, the service said: {refusal.Error ?? "(ok, so the null was the wire's)"}");
        }

        Assert.Equal(PaintedBytes, blurred);
        Assert.Equal(PaintedBytes, await _store!.GetRedactedFrameImageAsync("f1"));
    }

    [Fact]
    public async Task AFrameThatIsGoneIsNullNotAnException()
    {
        // The screen is already open when this is asked; a throw here takes the window with it.
        var frames = await FramesAsync();

        Assert.Null(await frames.ImageAsync(FrameNamed("nope")));
        Assert.Null(await frames.BlurAsync("nope", new MaskedRegion { X = 0, Y = 0, Width = 1, Height = 1, Kind = MaskKind.UserBlur }));
        Assert.Null(await frames.LoadSessionAsync("nope"));
    }

    [Fact]
    public async Task EditsGoThroughThePipeAndLand()
    {
        var frames = await FramesAsync();

        await frames.SetIncludedAsync("f1", false);
        await frames.SaveDraftAsync("s1", Draft() with { Problem = "edited" });
        var deleted = await frames.DeleteAsync("f1");

        Assert.True(deleted);
        var session = (await _store!.LoadSessionAsync("s1"))!;
        Assert.Equal("edited", session.Draft!.Problem);
        Assert.Empty(session.Frames);
    }

    [Fact]
    public async Task AnInclusionSaysWhetherTheServiceRecordedIt()
    {
        // The store's flag, not the strip's, decides what publish attaches (ST-078). A write that did not
        // land has to say so, or the strip goes on promising a frame the service will not send.
        var frames = await FramesAsync();

        Assert.True(await frames.SetIncludedAsync("f1", true));
        Assert.False(await frames.SetIncludedAsync("nope", true));
    }

    [Fact]
    public async Task AnInclusionWithNoServiceIsNotRecorded()
    {
        var frames = new PipeReviewFrames(new CaptureConnection(
            new ShellState(),
            _ => throw new IOException("no service"),
            CaptureConnection.Never));

        Assert.False(await frames.SetIncludedAsync("f1", true));
    }

    [Fact]
    public async Task ALongSessionOpensOverThePipeAndTheConnectionHolds()
    {
        // E-20 end to end. The service used to write the whole session, every frame's OCR text inside it,
        // as one message; past 1 MiB the write threw, the server dropped the UI's connection, and the
        // pane got null. Now the session crosses without the text and the same connection carries on.
        var frames = await FramesAsync();
        await SeedAsync("long", frames: 600, ocrChars: 2_000, segments: 200, segmentChars: 120);

        var session = await frames.LoadSessionAsync("long");

        Assert.NotNull(session);
        Assert.Equal(600, session.Frames.Count);
        Assert.Equal(RedactedBytes, await frames.ImageAsync(FrameNamed("f1")));

        // And the text the session left behind is there for the frame that is opened.
        Assert.Equal(new string('x', 2_000), await frames.OcrTextAsync(session.Frames[^1]));
    }

    [Fact]
    public async Task AFramesTextCrossesOnItsOwnAndAMissingOneIsNull()
    {
        var frames = await FramesAsync();
        var session = (await frames.LoadSessionAsync("s1"))!;

        Assert.Null(session.Frames[0].OcrText);
        Assert.Equal("t", await frames.OcrTextAsync(session.Frames[0]));
        Assert.Null(await frames.OcrTextAsync(FrameNamed("nope")));
    }

    [Fact]
    public async Task ASessionTooLargeForThePipeIsNullAndTheConnectionHolds()
    {
        // Refused with a reason on the service side, which is a null on this one, and the next question
        // on the same connection is answered. Before, the oversized write closed the pipe under the UI.
        var frames = await FramesAsync();
        await SeedAsync("huge", frames: 1, ocrChars: 10, segments: 600, segmentChars: 2_000);

        Assert.Null(await frames.LoadSessionAsync("huge"));

        Assert.Equal(RedactedBytes, await frames.ImageAsync(FrameNamed("f1")));
        var refusal = await _connection!.SendAsync(id => new GetSessionCommand { RequestId = id, SessionId = "huge" });
        Assert.False(refusal.Ok);
        Assert.Contains("too large", refusal.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly byte[] RedactedBytes = [9, 8, 7, 6];
    private static readonly byte[] PaintedBytes = [0xCC, 0xCC];

    private async Task<PipeReviewFrames> FramesAsync()
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        await _store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await _store.SaveRedactedFrameAsync("s1", new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(RedactedBytes, "t", [], false, DateTimeOffset.UnixEpoch));
        await _store.SaveDraftAsync("s1", Draft());
        await _store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));

        var handler = new ReviewOnlyController(new ReviewCommands(_store, new PaintingMasker(PaintedBytes)));
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, new AcceptAll(), handler, _store, "0.0.1-test");
        _server.Start();

        _connection = new CaptureConnection(
            new ShellState(),
            async ct => await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", ct: ct),
            // Keep trying until the server is listening. IpcServer.Start creates its pipe on a background
            // task, and with CaptureConnection.Never a first attempt that ran ahead of it on a busy runner
            // was the only attempt: both pipe test classes failed together, about one hosted run in ten.
            _ => TimeSpan.FromMilliseconds(50));
        await _connection.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!_connection.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection did not come up");
            await Task.Delay(10);
        }

        return new PipeReviewFrames(_connection);
    }

    private async Task SeedAsync(string sessionId, int frames, int ocrChars, int segments, int segmentChars)
    {
        var store = _store!;
        await store.CreateSessionAsync(new NewSession(sessionId, DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        var ocr = new string('x', ocrChars);
        for (var i = 0; i < frames; i++)
        {
            var staged = new StagedFrame(Guid.NewGuid().ToString(), i * 1_000L, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 });
            await store.SaveRedactedFrameAsync(sessionId, staged, new RedactionOutcome(new byte[] { 2 }, ocr, [], false, DateTimeOffset.UnixEpoch));
        }

        var words = new string('w', segmentChars);
        for (var i = 0; i < segments; i++)
        {
            await store.AppendTranscriptAsync(sessionId, new TranscriptSegment { Id = Guid.NewGuid().ToString(), TsMs = i * 5_000L, EndMs = (i * 5_000L) + 4_000, Speaker = Speaker.Tech, Text = words });
        }

        await store.FinalizeSessionAsync(sessionId, new FinalizeInfo(frames * 1_000L, false));
    }

    private static Frame FrameNamed(string id) => new()
    {
        Id = id,
        TsMs = 1,
        Trigger = FrameTrigger.Click,
        Image = $"frames/{id}.jpg",
        Width = 1600,
        Height = 900,
        RedactionPending = false,
        RedactedAt = DateTimeOffset.UnixEpoch,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    /// <summary>A handler that knows only the review commands, which is all this pipe is asked.</summary>
    private sealed class ReviewOnlyController(ReviewCommands review) : IIpcCommandHandler
    {
        public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

        public CapabilitiesReported CurrentCapabilities => throw new NotSupportedException();

        public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            await review.HandleAsync(command, ct)
                ?? new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Not a review command." };

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default) =>
            review.ReplyToAsync(command, ct);
    }

    private sealed class PaintingMasker(byte[] painted) : IFrameMasker
    {
        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) => new(painted, 1600, 900);
    }

    private sealed class AcceptAll : IClientVerifier
    {
        public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

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
