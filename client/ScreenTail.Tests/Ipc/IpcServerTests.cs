using System.Diagnostics;
using System.IO.Pipes;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Ipc;

/// <summary>ST-004 over real pipes (Unix sockets here, named pipes on Windows): handshake, auth, reattach, latency.</summary>
public sealed class IpcServerTests : IAsyncDisposable
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private readonly string _pipeName = IpcPipeNames.ForUser("test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _token = IpcToken.Generate();
    private readonly FakeController _controller = new();
    private readonly InMemoryAudit _audit = new();
    private IpcServer? _server;

    [Fact]
    public async Task HandshakeCarriesStateAndCommandsGetMatchingResults()
    {
        _controller.State = Recording("s1");
        await StartServerAsync();

        await using var client = await ConnectAsync();
        var first = await client.SendAsync(id => new StartCommand { RequestId = id });
        var second = await client.SendAsync(id => new PauseCommand { RequestId = id });

        Assert.Equal(CaptureStates.Recording, client.State.State);
        Assert.Equal("s1", client.State.SessionId);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal([typeof(StartCommand), typeof(PauseCommand)], _controller.Received.Select(c => c.GetType()));
    }

    [Fact]
    public async Task RoundTripIsUnderTenMilliseconds()
    {
        await StartServerAsync();
        await using var client = await ConnectAsync();
        for (var i = 0; i < 20; i++)
        {
            await client.SendAsync(id => new GetStateCommand { RequestId = id });
        }

        var samples = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await client.SendAsync(id => new GetStateCommand { RequestId = id });
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var p95 = samples[(int)(samples.Count * 0.95)];
        Assert.True(median < 10, $"median round trip {median:0.00} ms (p95 {p95:0.00} ms)");
    }

    [Fact]
    public async Task UiKilledMidSessionServiceKeepsRecordingAndReattachesWithinTwoSeconds()
    {
        _controller.State = Recording("s1");
        await StartServerAsync();

        var first = await ConnectAsync();
        Assert.Equal("s1", first.State.SessionId);

        // "Kill" the UI: drop the pipe without any goodbye.
        await first.DisposeAsync();

        var restart = Stopwatch.StartNew();
        await using var second = await ConnectAsync();
        restart.Stop();

        Assert.True(restart.Elapsed < TimeSpan.FromSeconds(2), $"reattach took {restart.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(CaptureStates.Recording, second.State.State);
        Assert.Equal("s1", second.State.SessionId);
        Assert.Empty(_controller.Received); // nothing about the UI's death reached the capture side
        await WaitUntilAsync(() => _server!.ConnectedClients == 1);
    }

    [Fact]
    public async Task UnverifiedClientIsRejectedAndAudited()
    {
        await StartServerAsync(new FakeVerifier("unsigned"));

        var ex = await Assert.ThrowsAsync<IpcRejectedException>(() => ConnectAsync());

        Assert.Equal(RejectReasons.UnverifiedClient, ex.Reason);
        await WaitUntilAsync(() => _audit.Types.Contains("ipc_rejected_" + RejectReasons.UnverifiedClient));
        Assert.Equal(0, _server!.ConnectedClients);
    }

    [Fact]
    public async Task WrongTokenIsRejectedAndAudited()
    {
        await StartServerAsync();

        var ex = await Assert.ThrowsAsync<IpcRejectedException>(() => IpcClient.ConnectAsync(_pipeName, IpcToken.Generate(), "ui", serverVerifier: null));

        Assert.Equal(RejectReasons.BadToken, ex.Reason);
        await WaitUntilAsync(() => _audit.Types.Contains("ipc_rejected_" + RejectReasons.BadToken));
    }

    [Fact]
    public async Task ContractMismatchIsRejected()
    {
        await StartServerAsync();

        var reply = await RawHandshakeAsync(new HelloCommand { RequestId = 1, ContractVersion = IpcContract.Version + 1, Token = IpcToken.Encode(_token), ClientName = "old-ui" });

        Assert.Equal(RejectReasons.ContractMismatch, Assert.IsType<Rejected>(reply).Reason);
    }

    [Fact]
    public async Task FirstMessageMustBeHello()
    {
        await StartServerAsync();

        var reply = await RawHandshakeAsync(new StartCommand { RequestId = 1 });

        Assert.Equal(RejectReasons.Protocol, Assert.IsType<Rejected>(reply).Reason);
        await WaitUntilAsync(() => _audit.Types.Contains("ipc_rejected_" + RejectReasons.Protocol));
    }

    [Fact]
    public async Task EventsReachEveryConnectedClient()
    {
        await StartServerAsync();
        await using var tray = await ConnectAsync();
        await using var hud = await ConnectAsync();
        var trayGot = new TaskCompletionSource<CaptureStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hudGot = new TaskCompletionSource<CaptureStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        tray.StateChanged += s => trayGot.TrySetResult(s);
        hud.StateChanged += s => hudGot.TrySetResult(s);
        await WaitUntilAsync(() => _server!.ConnectedClients == 2);

        await _server!.BroadcastAsync(new StateChanged { State = Recording("s9") });

        var results = await Task.WhenAll(trayGot.Task.WaitAsync(Soon), hudGot.Task.WaitAsync(Soon));
        Assert.All(results, s => Assert.Equal("s9", s.SessionId));
        Assert.Equal("s9", tray.State.SessionId);
    }

    [Fact]
    public async Task ServerShutdownTellsClients()
    {
        await StartServerAsync();
        var client = await ConnectAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();

        await _server!.DisposeAsync();
        _server = null;

        await disconnected.Task.WaitAsync(Soon);
        Assert.False(client.IsConnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task APeerThatSaysNothingDoesNotHoldAConnectionForEver()
    {
        // Before this there was no handshake deadline. A same-user process could connect, send nothing,
        // and hold a pipe instance and a parked task until the service stopped — thousands of them, until
        // the service ran out of the handles it needs to accept the real UI.
        await StartServerAsync();

        await using var silent = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await silent.ConnectAsync((int)Soon.TotalMilliseconds);

        // The server drops it on its own; the read ends when the far side closes.
        var buffer = new byte[1];
        var read = await silent.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, read);
        Assert.Equal(0, _server!.ConnectedClients);
        Assert.True(_server.RefusedConnections > 0, "the connection was dropped without being counted");
    }

    [Fact]
    public async Task ARejectedPeerCannotWriteToTheStoreAsFastAsItCanConnect()
    {
        // Every rejection used to INSERT a row, which gave an unauthenticated local peer an unbounded
        // write primitive into the encrypted database: audit rows are never pruned, so it grew until the
        // disk filled and every real store write starved behind the gate it held. One row a minute per
        // reason still records that somebody is trying, and how often.
        await StartServerAsync();

        for (var i = 0; i < 25; i++)
        {
            // Either outcome is a refusal: rejected with a reason, or dropped for being one of too many
            // handshakes at once. What matters is that neither reaches the store.
            await Assert.ThrowsAnyAsync<Exception>(
                () => IpcClient.ConnectAsync(_pipeName, IpcToken.Generate(), "ui", serverVerifier: null));
        }

        await WaitUntilAsync(() => _audit.Types.Contains("ipc_rejected_" + RejectReasons.BadToken));
        Assert.Equal(1, _audit.Types.Count(t => t == "ipc_rejected_" + RejectReasons.BadToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheTokenIsNotSentToAServerTheUiDoesNotTrust()
    {
        // ST-012. The handshake used to be one-sided: connect, write the token, then find out who caught
        // it. Anything that can create this pipe name first - a per-user pipe, so no privilege is needed -
        // receives the session token and can then tell the UI whatever it likes about capture state. INV-4
        // fails in the worst direction: "not recording" on screen while recording continues.
        //
        // So the test is the impostor. A bare pipe server, no service behind it, and the question is
        // whether a single byte arrives.
        var pipeName = IpcPipeNames.ForUser("impostor-" + Guid.NewGuid().ToString("N"));
        await using var impostor = IpcServer.DefaultPipeFactory(pipeName)();
        var listening = impostor.WaitForConnectionAsync();
        var refusing = new FakeServerVerifier("modified_binary");

        var refused = await Assert.ThrowsAsync<IpcUntrustedServerException>(
            () => IpcClient.ConnectAsync(pipeName, _token, "test-ui", refusing, "0.0.1", Soon));

        Assert.Equal("modified_binary", refused.Reason);
        Assert.True(refusing.Asked, "the verifier was never consulted");

        // The connection itself is expected — that is how the verifier gets something to inspect. What
        // must not have happened is a write.
        await listening.WaitAsync(Soon);
        var read = new byte[64];
        using var noMore = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var arrived = 0;
        try
        {
            arrived = await impostor.ReadAsync(read, noMore.Token).AsTask();
        }
        catch (OperationCanceledException)
        {
            // Nothing came, which is the point: the client hung up without writing.
        }

        Assert.Equal(0, arrived);
    }

    [Fact]
    public async Task AVerifiedServerGetsTheUsualHandshake()
    {
        // The other half: a verifier that approves must not change anything a technician sees.
        _controller.State = Recording("s1");
        await StartServerAsync();
        var trusting = new FakeServerVerifier(null);

        await using var client = await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", trusting, "0.0.1", Soon);

        Assert.True(trusting.Asked);
        Assert.Equal(CaptureStates.Recording, client.State.State);
        Assert.True((await client.SendAsync(id => new GetStateCommand { RequestId = id })).Ok);
    }

    private sealed class FakeServerVerifier(string? refusal) : IServerVerifier
    {
        public bool Asked { get; private set; }

        public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default)
        {
            Asked = true;
            return ValueTask.FromResult(refusal);
        }
    }

    private async Task StartServerAsync(IClientVerifier? verifier = null)
    {
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, verifier ?? new FakeVerifier(null), _controller, _audit, "0.0.1-test");
        _server.Start();
        await Task.Yield();
    }

    private Task<IpcClient> ConnectAsync() => IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1");

    private async Task<IpcEvent?> RawHandshakeAsync(IpcCommand first)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)Soon.TotalMilliseconds);
        await IpcFraming.WriteAsync(pipe, first);
        return await IpcFraming.ReadAsync<IpcEvent>(pipe);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Soon;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    private static CaptureStateSnapshot Recording(string sessionId) => new()
    {
        State = CaptureStates.Recording,
        SessionId = sessionId,
        ElapsedMs = 761_000,
        RemoteTool = "screenconnect",
        PendingRedactions = 3,
    };

    private sealed class FakeController : IIpcCommandHandler
    {
        public CapabilitiesReported CurrentCapabilities => new AlwaysCapableProbe().Probe().ToWire();

        public CaptureStateSnapshot State { get; set; } = CaptureStateSnapshot.Idle;

        public List<IpcCommand> Received { get; } = [];

        public CaptureStateSnapshot CurrentState => State;

        public Task<CommandResult> HandleAsync(IpcCommand command, CancellationToken ct = default)
        {
            Received.Add(command);
            return Task.FromResult(new CommandResult { RequestId = command.RequestId, Ok = true });
        }
    }

    private sealed class FakeVerifier(string? reason) : IClientVerifier
    {
        public ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default) => ValueTask.FromResult(reason);
    }

    private sealed class InMemoryAudit : IAuditLog
    {
        private readonly List<string> _types = [];

        public IReadOnlyList<string> Types
        {
            get
            {
                lock (_types)
                {
                    return _types.ToList();
                }
            }
        }

        public Task RecordAsync(string type, string? sessionId = null, long? count = null, CancellationToken ct = default)
        {
            lock (_types)
            {
                _types.Add(type);
            }

            return Task.CompletedTask;
        }
    }
}
