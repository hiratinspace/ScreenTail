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
            CaptureConnection.Never);
        await _connection.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!_connection.IsConnected)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection did not come up");
            await Task.Delay(10);
        }

        return new PipeReviewFrames(_connection);
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
