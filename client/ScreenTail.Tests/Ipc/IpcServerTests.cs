using System.Diagnostics;
using System.IO.Pipes;
using ScreenTail.Core.Audit;
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

        var ex = await Assert.ThrowsAsync<IpcRejectedException>(() => IpcClient.ConnectAsync(_pipeName, IpcToken.Generate(), "ui"));

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

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private async Task StartServerAsync(IClientVerifier? verifier = null)
    {
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), _token, verifier ?? new FakeVerifier(null), _controller, _audit, "0.0.1-test");
        _server.Start();
        await Task.Yield();
    }

    private Task<IpcClient> ConnectAsync() => IpcClient.ConnectAsync(_pipeName, _token, "test-ui", "0.0.1");

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
