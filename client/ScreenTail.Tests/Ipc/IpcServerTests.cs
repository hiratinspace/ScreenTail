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
    public async Task SilentConnectionsCannotLockTheRealUiOut()
    {
        // ST-085 (weaknesses P1-6). The handshake limit was one global counter with no fairness, so a
        // same-user process — explicitly in scope — could open sixteen connections, send nothing, and
        // refill every slot as the timeout reaped them. The real UI then saw the pipe closed during its
        // handshake, could not see or change capture state, and INV-4's indicator became unreachable
        // without touching capture at all.
        //
        // Patient, so the squatters are still squatting when the UI arrives: on a busy runner the real
        // deadline could reap them first and this test would be proving nothing.
        await StartServerAsync(handshakeTimeout: Patient);
        var ct = TestContext.Current.CancellationToken;

        // Twenty silent peers: more than the global limit, all from one process.
        var squatters = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(200, ct);
                    squatters.Add(pipe);
                }
                catch (TimeoutException)
                {
                    await pipe.DisposeAsync();
                }
            }

            // The UI still gets in, while every one of them is still holding its pipe open and saying
            // nothing.
            await using var ui = await ConnectAsync();
            Assert.Equal(CaptureStates.Idle, ui.State.State);
        }
        finally
        {
            foreach (var pipe in squatters)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AQuestionIsAnsweredWithItsOwnEventCarryingTheRequestId()
    {
        // ST-085. Commands were answered by a result and nothing else, so a reply type with its own shape
        // had no way home: get_capabilities has had one since ST-021 and would have hung the first time
        // anything asked, because the client only ever completed a CommandResult. The reply now carries
        // the request id like everything else, and one rule in the read loop matches them up.
        _controller.Answer = new SessionsListed
        {
            Sessions = [new SessionRow
            {
                Id = "s1",
                StartedAt = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero),
                DurationMs = 61_000,
                Status = "draft",
                Tool = "screenconnect",
                Frames = 7,
                FramesPurged = 1,
            }],
        };
        await StartServerAsync();
        await using var ui = await ConnectAsync();

        var reply = await ui.RequestAsync<SessionsListed>(
            id => new ListSessionsCommand { RequestId = id },
            TestContext.Current.CancellationToken);

        var row = Assert.Single(reply.Sessions);
        Assert.Equal("s1", row.Id);
        Assert.Equal(7, row.Frames);
    }

    [Fact]
    public async Task AQuestionAnsweredWithOnlyAResultFailsRatherThanHanging()
    {
        // No answer set, so the handler treats the question as an instruction and returns a plain
        // result. The client must turn that into an error the caller can show, not wait for an event
        // that is never coming: a screen that hangs on open is worse than one that says it could not
        // ask. The earlier version of this test sent the question through SendAsync, which wants a
        // result and so could never have seen RequestAsync hang (2026-10-12 review, T-81).
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<IpcProtocolException>(
            () => ui.RequestAsync<SessionsListed>(id => new ListSessionsCommand { RequestId = id }, ct).WaitAsync(Soon, ct));

        Assert.Contains(nameof(SessionsListed), refused.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CommandResult), refused.Message, StringComparison.Ordinal);
        Assert.True(ui.IsConnected);
    }

    [Fact]
    public async Task AStateChangeDuringTheHandshakeStillReachesTheUi()
    {
        // 2026-10-12 review, S-20 and T-74. The HelloAck's state was read before the connection joined
        // the broadcast set, so a transition in that gap was broadcast to everyone but the client that
        // was connecting. It then showed the old state until the next transition: idle while the
        // service recorded (INV-4). Here the service starts recording the instant the snapshot for the
        // ack is taken, and broadcasts it, exactly as the host does.
        var recording = Recording("s1");
        _controller.AfterStateIsRead = () =>
        {
            _controller.State = recording;
            _ = _server!.BroadcastAsync(new StateChanged { State = recording });
        };
        await StartServerAsync();

        await using var ui = await ConnectAsync();

        Assert.True(_controller.StateHookFired, "the handshake never read the state, so the gap was not exercised");
        await WaitUntilAsync(() => ui.State.State == CaptureStates.Recording);
        Assert.Equal("s1", ui.State.SessionId);
        Assert.Equal(1, _server!.ConnectedClients);
    }

    [Fact]
    public async Task TheServerKeepsItsOwnCopyOfTheToken()
    {
        // T-70. CaptureHost clears its array as soon as the server has it, so a server that kept the
        // caller's array would compare every hello against zeroes and lock every UI out.
        var handed = (byte[])_token.Clone();
        _server = new IpcServer(IpcServer.DefaultPipeFactory(_pipeName), handed, new FakeVerifier(null), _controller, _audit, "0.0.1-test");
        Array.Clear(handed);
        _server.Start();

        await using var ui = await ConnectAsync();

        Assert.True(ui.IsConnected);
        Assert.Equal(1, _server.ConnectedClients);
    }

    [Fact]
    public async Task AFailedPipeInstanceIsRetriedRatherThanEndingTheServer()
    {
        // T-71. The accept loop is an unobserved Task.Run; a pipe factory that throws on resource
        // pressure must cost one retry, not the service's ability to accept the UI ever again.
        var calls = 0;
        var real = IpcServer.DefaultPipeFactory(_pipeName);
        await StartServerAsync(pipeFactory: () => ++calls == 1 ? throw new IOException("out of handles") : real());

        await using var ui = await IpcClient.ConnectAsync(_pipeName, _token, "test-ui", serverVerifier: null, "0.0.1", Soon);

        Assert.True(ui.IsConnected);
        Assert.True(calls >= 2, $"the factory was called {calls} time(s)");
        Assert.Equal(1, _server!.RefusedConnections);
        Assert.True((await ui.SendAsync(id => new GetStateCommand { RequestId = id })).Ok);
    }

    [Fact]
    public async Task AClientThatStopsReadingDoesNotHoldUpTheOthers()
    {
        // T-72. A broadcast goes to every client at once, each with its own deadline. One at a time, a
        // peer that had stopped reading held every state change to every other window, and the pipe has
        // no output buffer to speak of: "stopped reading" blocks the write rather than filling a queue.
        // The HUD's pill is one of those other windows (INV-4).
        await StartServerAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var hud = await ConnectAsync();
        var hudGot = new TaskCompletionSource<CaptureStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        hud.StateChanged += s => hudGot.TrySetResult(s);

        // Authenticated, then deaf: it never reads another byte.
        await using var deaf = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await deaf.ConnectAsync((int)Soon.TotalMilliseconds, ct);
        await IpcFraming.WriteAsync<IpcCommand>(deaf, new HelloCommand { RequestId = 0, ContractVersion = IpcContract.Version, Token = IpcToken.Encode(_token), ClientName = "deaf" }, ct);
        Assert.IsType<HelloAck>(await IpcFraming.ReadAsync<IpcEvent>(deaf, ct));
        await WaitUntilAsync(() => _server!.ConnectedClients == 2);

        // Far more than any pipe or socket buffers, so the deaf peer's write is stuck for its whole deadline.
        var large = Recording(new string('s', 768 * 1024));
        var started = Stopwatch.GetTimestamp();
        var broadcast = _server!.BroadcastAsync(new StateChanged { State = large }, ct);

        var seen = await hudGot.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        Assert.Equal(large.SessionId, seen.SessionId);
        Assert.False(broadcast.IsCompleted, "the deaf peer did not block its write, so this run proves nothing about waiting");

        // The broadcast still ends, within the per-client deadline, and the HUD is still being served.
        await broadcast.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8), "the broadcast was held past its deadline");
        Assert.True((await hud.SendAsync(id => new GetStateCommand { RequestId = id }, ct).WaitAsync(Soon, ct)).Ok);
    }

    [Fact]
    public async Task AnAuthenticatedClientOutlivesTheHandshakeDeadline()
    {
        // T-73. The handshake's deadline is for a peer that has not said hello. A client that has must
        // be served on the server's own lifetime, or every UI would be cut off two seconds in. The
        // deadline is shortened here so the test does not have to sit through the real two seconds.
        var deadline = TimeSpan.FromMilliseconds(300);
        await StartServerAsync(handshakeTimeout: deadline);
        var ct = TestContext.Current.CancellationToken;
        await using var ui = await ConnectAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Disconnected += () => disconnected.TrySetResult();

        await Task.Delay(deadline * 4, ct);

        Assert.False(disconnected.Task.IsCompleted, "the server dropped an authenticated client at the handshake deadline");
        Assert.True(ui.IsConnected);
        Assert.True((await ui.SendAsync(id => new GetStateCommand { RequestId = id }, ct).WaitAsync(Soon, ct)).Ok);
        Assert.Equal(1, _server!.ConnectedClients);
    }

    [Fact]
    public async Task EachConnectionIsItsOwnCallerForItsWholeLife()
    {
        // T-69. Confirmation tokens are bound to the caller the handler is given. Guid.Empty for every
        // connection would make a token a bearer token; a fresh id per command would mean no token
        // could ever be spent by the window that asked for it, on either lane.
        _controller.Answer = new ConfirmationIssued { Token = "t", Phrase = "DISCARD" };
        _controller.AnswersTo = typeof(RequestConfirmationCommand);
        await StartServerAsync();
        var ct = TestContext.Current.CancellationToken;
        await using var tray = await ConnectAsync();
        await using var hud = await ConnectAsync();

        // Read-loop lane, worker lane and a question, from each.
        await tray.SendAsync(id => new StartCommand { RequestId = id }, ct);
        await tray.SendAsync(id => new GetSettingsCommand { RequestId = id }, ct);
        await tray.RequestAsync<ConfirmationIssued>(id => new RequestConfirmationCommand { RequestId = id, Action = "discard_session" }, ct);
        await hud.SendAsync(id => new PauseCommand { RequestId = id }, ct);
        await hud.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await hud.RequestAsync<ConfirmationIssued>(id => new RequestConfirmationCommand { RequestId = id, Action = "discard_session" }, ct);

        var calls = _controller.Calls;
        var trayCallers = calls.Where(c => c.Command is StartCommand or GetSettingsCommand).Select(c => c.Caller).Distinct().ToList();
        var hudCallers = calls.Where(c => c.Command is PauseCommand or GetIntegrationsCommand).Select(c => c.Caller).Distinct().ToList();
        var askers = calls.Where(c => c.Command is RequestConfirmationCommand).Select(c => c.Caller).Distinct().ToList();

        var trayId = Assert.Single(trayCallers);
        var hudId = Assert.Single(hudCallers);
        Assert.NotEqual(Guid.Empty, trayId);
        Assert.NotEqual(Guid.Empty, hudId);
        Assert.NotEqual(trayId, hudId);
        Assert.Equal(2, askers.Count);
        Assert.Contains(trayId, askers);
        Assert.Contains(hudId, askers);
    }

    [Fact]
    public async Task ARequestInFlightFailsWhenTheServiceGoesAway()
    {
        // T-68. A request the service never answers because it went away has to fail, not wait: the
        // Review and Settings screens await these, and a screen that waits for ever is a window that
        // never opens. Both kinds are in flight here, one inside its handler and one queued behind it.
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var held = ui.RequestAsync<IntegrationsListed>(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        var queued = ui.SendAsync(id => new GetSettingsCommand { RequestId = id }, ct);

        await _server!.DisposeAsync();
        _server = null;

        await Assert.ThrowsAsync<IOException>(() => held.WaitAsync(Soon, ct));
        await Assert.ThrowsAsync<IOException>(() => queued.WaitAsync(Soon, ct));
        Assert.False(ui.IsConnected);
    }

    [Fact]
    public async Task AnAnswerTooLargeForThePipeIsRefusedAndTheConnectionStaysUp()
    {
        // 2026-10-12 review, E-2. Writing a reply over the frame limit threw out of the serve loop and the
        // server dropped the connection: the UI's only pipe, the one that carries the indicator heartbeat.
        // An answer that cannot be sent is a refusal with a reason, and the pipe carries on.
        _controller.Answer = new SessionsListed
        {
            Sessions = [.. Enumerable.Range(0, 12_000).Select(i => new SessionRow
            {
                Id = $"session-{i:D8}-{new string('x', 40)}",
                StartedAt = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero),
                DurationMs = 61_000,
                Status = "draft",
                Frames = 7,
                FramesPurged = 0,
            })],
        };
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var refused = await Assert.ThrowsAsync<IpcProtocolException>(
            () => ui.RequestAsync<SessionsListed>(id => new ListSessionsCommand { RequestId = id }, ct));

        Assert.Contains("too large", refused.Message, StringComparison.Ordinal);
        Assert.True(ui.IsConnected);
        Assert.True((await ui.SendAsync(id => new GetStateCommand { RequestId = id }, ct)).Ok);
        Assert.Equal(1, _server!.ConnectedClients);
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
    public async Task VerifyingTheRealUiDoesNotMakeItLookSilent()
    {
        // 2026-09-19 review, and the half of weaknesses P1-6 that was still open. An entry left the
        // eviction table only after the *whole* handshake — which includes verifying the peer's
        // Authenticode signature, two WinVerifyTrust calls that take real time on a cold file.
        //
        // So the genuine UI counted as "silent" during its own verification, and a same-user process
        // opening connections in a loop could evict it. The table exists to stop exactly that lockout,
        // and it was the thing causing it.
        //
        // Patient, because the UI's own handshake is under way while twenty peers connect, and on a
        // busy runner that can take longer than the real deadline.
        var slow = new SlowVerifier(TimeSpan.FromMilliseconds(400));
        await StartServerAsync(slow, handshakeTimeout: Patient);
        var ct = TestContext.Current.CancellationToken;

        var squatters = new List<NamedPipeClientStream>();
        try
        {
            var connecting = ConnectAsync();

            // While the UI is inside the verifier, twenty peers connect and say nothing. Under the old
            // rule these filled the table and pushed the UI's own entry out of it.
            await slow.Reached.Task.WaitAsync(Soon, ct);
            for (var i = 0; i < 20; i++)
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(200, ct);
                    squatters.Add(pipe);
                }
                catch (TimeoutException)
                {
                    await pipe.DisposeAsync();
                }
            }

            await using var ui = await connecting;
            Assert.Equal(CaptureStates.Idle, ui.State.State);
        }
        finally
        {
            foreach (var pipe in squatters)
            {
                await pipe.DisposeAsync();
            }
        }
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

    [Fact]
    public async Task ASlowCommandDoesNotHoldUpTheIndicatorOrTheSessionControls()
    {
        // E-25 (2026-10-12 review). The server read a command, awaited its handler, wrote the reply, and
        // only then read the next. The HUD's two-second "the pill is on screen" shares the UI's one
        // connection with publish, search and the integration screens, which wait on the backend. A
        // backend call that ran past the indicator's grace left the pill on screen and the service
        // believing nothing showed it, so frames and typing were dropped as unindicated (INV-4, INV-6),
        // and Stop and Pause queued behind the same call.
        _controller.State = Recording("s1");
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);

        // What the HUD says every two seconds, and every button a technician reaches for mid-session.
        Func<int, IpcCommand>[] urgent =
        [
            id => new IndicatorShowingCommand { RequestId = id, X = 10, Y = 10, Width = 160, Height = 32 },
            id => new PauseCommand { RequestId = id },
            id => new ResumeCommand { RequestId = id },
            id => new MarkMomentCommand { RequestId = id },
            id => new GetStateCommand { RequestId = id },
            id => new RequestConfirmationCommand { RequestId = id, Action = "discard_session" },
            id => new StopCommand { RequestId = id },
            id => new StartCommand { RequestId = id },
            id => new DiscardCommand { RequestId = id },
        ];
        foreach (var build in urgent)
        {
            var answered = await ui.SendAsync(build, ct).WaitAsync(TimeSpan.FromSeconds(1), ct);
            Assert.True(answered.Ok);
        }

        Assert.False(slow.IsCompleted, "the slow command was answered before its handler finished");
        _controller.Release.TrySetResult();
        Assert.True((await slow.WaitAsync(Soon, ct)).Ok);
    }

    [Fact]
    public async Task CommandsQueuedBehindASlowOneRunInTheOrderTheyWereSent()
    {
        // Only the session's own commands overtake. Everything else keeps the order it was sent in, as
        // it always had: a save and the publish after it, or a setting and the read after it, must not
        // swap places because one of them waited on the network.
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        var search = ui.SendAsync(id => new SearchTicketsCommand { RequestId = id, Query = "48213" }, ct);
        await ui.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 0, Y = 0, Width = 1, Height = 1 }, ct).WaitAsync(Soon, ct);
        var settings = ui.SendAsync(id => new GetSettingsCommand { RequestId = id }, ct);
        await ui.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 0, Y = 0, Width = 1, Height = 1 }, ct).WaitAsync(Soon, ct);

        // Both have been read by now (the pipe is read in order and the indicator after each was
        // answered), and neither has run.
        Assert.False(search.IsCompleted);
        Assert.False(settings.IsCompleted);
        Assert.DoesNotContain(_controller.Received, c => c is SearchTicketsCommand or GetSettingsCommand);

        _controller.Release.TrySetResult();
        await Task.WhenAll(slow, search, settings).WaitAsync(Soon, ct);

        Assert.Equal(
            [typeof(GetIntegrationsCommand), typeof(SearchTicketsCommand), typeof(GetSettingsCommand)],
            _controller.Received.Where(c => c is not IndicatorShowingCommand).Select(c => c.GetType()));
    }

    [Fact]
    public async Task AClientCannotQueueUnboundedWorkBehindASlowCommand()
    {
        // The queue behind a slow command is bounded, and a command that does not fit is refused at
        // once rather than making the reader wait for room: a reader waiting for room is a reader not
        // hearing the indicator.
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        const int Extra = 4;
        var queued = Enumerable.Range(0, IpcServer.MostQueuedPerConnection + Extra)
            .Select(_ => ui.SendAsync(id => new GetSettingsCommand { RequestId = id }, ct))
            .ToList();

        var heartbeat = await ui.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 0, Y = 0, Width = 1, Height = 1 }, ct)
            .WaitAsync(TimeSpan.FromSeconds(1), ct);
        Assert.True(heartbeat.Ok);

        // The refusals come back at once. Waited for rather than counted on the spot, because the
        // client hands each answer to its caller on the thread pool. No more than the extra can ever
        // finish here: the rest are queued behind the held handler.
        await WaitUntilAsync(() => queued.Count(t => t.IsCompleted) == Extra);
        var refused = queued.Where(t => t.IsCompleted).Select(t => t.Result).ToList();
        Assert.All(refused, r => Assert.False(r.Ok));
        Assert.All(refused, r => Assert.Contains("busy", r.Error, StringComparison.OrdinalIgnoreCase));

        _controller.Release.TrySetResult();
        var all = await Task.WhenAll(queued).WaitAsync(Soon, ct);
        Assert.True((await slow.WaitAsync(Soon, ct)).Ok);
        Assert.Equal(IpcServer.MostQueuedPerConnection, all.Count(r => r.Ok));
        Assert.Equal(IpcServer.MostQueuedPerConnection, _controller.Received.Count(c => c is GetSettingsCommand));
    }

    [Fact]
    public async Task NothingQueuedRunsAfterTheClientLeaves()
    {
        // An instruction from a window that has gone has nobody to tell how it went, and the old loop
        // never read it at all. Queued work is dropped with the connection rather than run blind.
        //
        // What stops it here is mostly the reply to the held command: it has nowhere to go once the
        // client has left, and that failure ends the worker before it reaches the next item. The check
        // between items is a backstop behind that, and this test does not tell the two apart.
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        _ = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        _ = ui.SendAsync(id => new DeleteFrameCommand { RequestId = id, FrameId = "f1" }, ct);
        await ui.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 0, Y = 0, Width = 1, Height = 1 }, ct).WaitAsync(Soon, ct);

        await ui.DisposeAsync();

        // Long enough for the reader to see the far end close; a local socket says so at once.
        await Task.Delay(250, ct);
        _controller.Release.TrySetResult();

        await WaitUntilAsync(() => _server!.ConnectedClients == 0);
        Assert.DoesNotContain(_controller.Received, c => c is DeleteFrameCommand);
    }

    [Fact]
    public async Task AHandlerThatFailsOffTheReadLoopStillEndsTheConnection()
    {
        // A handler that threw used to end the connection, and the UI reconnected. Moving the work off
        // the read loop must not turn that into a connection that reads on and never answers again.
        _controller.FailsOn = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Disconnected += () => disconnected.TrySetResult();

        var failed = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);

        await disconnected.Task.WaitAsync(Soon, ct);
        await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(Soon, ct));
        await WaitUntilAsync(() => _server!.ConnectedClients == 0);
    }

    [Fact]
    public async Task AFailedSessionCommandClosesTheConnectionWithoutWaitingForASlowOne()
    {
        // Round 1 review of E-25. A session command that fails on the read loop ends the reading, but
        // the pipe stayed open until the slow command behind it finished. Nothing read it in that time
        // and the client still looked connected, so it did not reconnect: the heartbeat, Stop, Pause and
        // Mark all hung for as long as the backend took. The old loop closed the connection at once.
        _controller.State = Recording("s1");
        _controller.Holds = typeof(GetIntegrationsCommand);
        _controller.FailsOn = typeof(PauseCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Disconnected += () => disconnected.TrySetResult();

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        var failed = ui.SendAsync(id => new PauseCommand { RequestId = id }, ct);

        await disconnected.Task.WaitAsync(Soon, ct);
        Assert.False(_controller.Release.Task.IsCompleted, "the connection closed only once the slow command was released");
        await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(Soon, ct));
        await Assert.ThrowsAsync<IOException>(() => slow.WaitAsync(Soon, ct));

        // A fresh connection is answered at once, and the held command's late reply goes nowhere.
        await using var again = await ConnectAsync();
        Assert.True((await again.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 0, Y = 0, Width = 1, Height = 1 }, ct).WaitAsync(TimeSpan.FromSeconds(1), ct)).Ok);
        _controller.Release.TrySetResult();
        await WaitUntilAsync(() => _server!.ConnectedClients == 1);
    }

    [Fact]
    public async Task ASettingChangeIsAppliedBeforeASessionCommandSentAfterIt()
    {
        // Round 1 review of E-25. A Start from the tray or the hotkey does not wait for its answer, so a
        // Save followed by a Start used to be applied in that order. With the settings behind a slow
        // command and the Start on the read loop, capture could begin under the excluded processes and
        // redaction choices the technician had just changed. Both settings writes are local files, so
        // they are answered on the read loop beside the session's own commands.
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        var privacy = ui.SendAsync(id => new SetSettingsCommand { RequestId = id, Settings = new ScreenTail.Shared.Settings.ClientSettings() }, ct);
        var capture = ui.SendAsync(id => new SetCaptureSettingsCommand { RequestId = id, Settings = new ScreenTail.Shared.Settings.CaptureSettings() }, ct);
        Assert.True((await ui.SendAsync(id => new StartCommand { RequestId = id }, ct).WaitAsync(TimeSpan.FromSeconds(1), ct)).Ok);

        Assert.True((await privacy.WaitAsync(TimeSpan.FromSeconds(1), ct)).Ok);
        Assert.True((await capture.WaitAsync(TimeSpan.FromSeconds(1), ct)).Ok);
        Assert.Equal(
            [typeof(GetIntegrationsCommand), typeof(SetSettingsCommand), typeof(SetCaptureSettingsCommand), typeof(StartCommand)],
            _controller.Received.Select(c => c.GetType()));

        _controller.Release.TrySetResult();
        Assert.True((await slow.WaitAsync(Soon, ct)).Ok);
    }

    [Fact]
    public async Task AnAnswerTooLargeForThePipeIsRefusedOnTheReadLoopToo()
    {
        // E-2 met E-25 in the rebase. The too-large refusal was written for the one loop there was; a
        // reply sent from the read loop, with the worker busy on a slow command, must be refused the
        // same way rather than end the connection under the command still running.
        _controller.Holds = typeof(GetIntegrationsCommand);
        _controller.AnswersTo = typeof(RequestConfirmationCommand);
        _controller.Answer = new ConfirmationIssued { Token = "t", Phrase = new string('x', IpcFraming.MaxMessageBytes) };
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);

        var refused = await Assert.ThrowsAsync<IpcProtocolException>(
            () => ui.RequestAsync<ConfirmationIssued>(id => new RequestConfirmationCommand { RequestId = id, Action = "discard_session" }, ct)
                .WaitAsync(Soon, ct));

        Assert.Contains("too large", refused.Message, StringComparison.Ordinal);
        Assert.False(slow.IsCompleted, "the refusal waited for the slow command");
        Assert.True(ui.IsConnected);

        _controller.Release.TrySetResult();
        Assert.True((await slow.WaitAsync(Soon, ct)).Ok);
        Assert.True((await ui.SendAsync(id => new GetStateCommand { RequestId = id }, ct)).Ok);
        Assert.Equal(1, _server!.ConnectedClients);
    }

    [Fact]
    public async Task TheReviewCommandsAddedSinceTheLanesWereSplitQueue()
    {
        // get_frame_text (#192) and set_frame_included (#190) are not on the read loop's list, so they
        // wait their turn behind a slow command like every other command off it. A frame's text can be
        // most of a megabyte, which is no thing for the indicator to wait behind.
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var slow = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);
        var text = ui.SendAsync(id => new GetFrameTextCommand { RequestId = id, FrameId = "f1" }, ct);
        var included = ui.SendAsync(id => new SetFrameIncludedCommand { RequestId = id, FrameId = "f1", Included = false }, ct);
        await ui.SendAsync(id => new IndicatorShowingCommand { RequestId = id, X = 0, Y = 0, Width = 1, Height = 1 }, ct).WaitAsync(Soon, ct);

        Assert.False(text.IsCompleted);
        Assert.False(included.IsCompleted);
        Assert.DoesNotContain(_controller.Received, c => c is GetFrameTextCommand or SetFrameIncludedCommand);

        _controller.Release.TrySetResult();
        await Task.WhenAll(slow, text, included).WaitAsync(Soon, ct);

        Assert.Equal(
            [typeof(GetIntegrationsCommand), typeof(GetFrameTextCommand), typeof(SetFrameIncludedCommand)],
            _controller.Received.Where(c => c is not IndicatorShowingCommand).Select(c => c.GetType()));
    }

    [Fact]
    public async Task StoppingTheServiceDoesNotWaitForASlowCommand()
    {
        _controller.Holds = typeof(GetIntegrationsCommand);
        await StartServerAsync();
        await using var ui = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Disconnected += () => disconnected.TrySetResult();

        _ = ui.SendAsync(id => new GetIntegrationsCommand { RequestId = id }, ct);
        await _controller.Holding.Task.WaitAsync(Soon, ct);

        await _server!.DisposeAsync().AsTask().WaitAsync(Soon, ct);
        _server = null;

        await disconnected.Task.WaitAsync(Soon, ct);
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

    /// <summary>
    /// Long enough that a busy runner cannot reap a silent peer, or time out the UI's own handshake,
    /// before a test has made its point. The product default stays two seconds.
    /// </summary>
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(60);

    private async Task StartServerAsync(
        IClientVerifier? verifier = null,
        Func<NamedPipeServerStream>? pipeFactory = null,
        TimeSpan? handshakeTimeout = null)
    {
        _server = new IpcServer(pipeFactory ?? IpcServer.DefaultPipeFactory(_pipeName), _token, verifier ?? new FakeVerifier(null), _controller, _audit, "0.0.1-test")
        {
            HandshakeTimeout = handshakeTimeout ?? IpcServer.DefaultHandshakeTimeout,
        };
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

        private readonly List<(IpcCommand Command, Guid Caller)> _calls = [];
        private Action? _afterStateIsRead;

        /// <summary>What reached the handler, in the order it arrived. A copy: two lanes add to it at once.</summary>
        public IReadOnlyList<IpcCommand> Received => [.. Calls.Select(c => c.Command)];

        /// <summary>Each command with the caller the server named for it.</summary>
        public IReadOnlyList<(IpcCommand Command, Guid Caller)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        /// <summary>
        /// Runs once, the first time the server reads the state, after the snapshot it will use has been
        /// taken: a transition landing the instant after the handshake looked.
        /// </summary>
        public Action? AfterStateIsRead
        {
            set => _afterStateIsRead = value;
        }

        public bool StateHookFired { get; private set; }

        public CaptureStateSnapshot CurrentState
        {
            get
            {
                var snapshot = State;
                if (Interlocked.Exchange(ref _afterStateIsRead, null) is { } hook)
                {
                    StateHookFired = true;
                    hook();
                }

                return snapshot;
            }
        }

        /// <summary>What a question is answered with, when a test sets one.</summary>
        public IpcEvent? Answer { get; set; }

        /// <summary>The question <see cref="Answer"/> answers. A list of sessions unless a test says otherwise.</summary>
        public Type AnswersTo { get; set; } = typeof(ListSessionsCommand);

        /// <summary>
        /// A command of this type waits inside the handler until <see cref="Release"/> is set, the way a
        /// publish waits on the backend.
        /// </summary>
        public Type? Holds { get; set; }

        /// <summary>Set once a held command is inside the handler.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>A command of this type fails inside the handler, the way a broken store would.</summary>
        public Type? FailsOn { get; set; }

        public async Task<CommandResult> HandleAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
        {
            lock (_calls)
            {
                _calls.Add((command, caller));
            }

            if (command.GetType() == Holds)
            {
                Holding.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            if (command.GetType() == FailsOn)
            {
                throw new IOException("The handler failed.");
            }

            return new CommandResult { RequestId = command.RequestId, Ok = true };
        }

        public Task<IpcEvent?> ReplyToAsync(IpcCommand command, Guid caller, CancellationToken ct = default)
        {
            if (Answer is null || command.GetType() != AnswersTo)
            {
                return Task.FromResult<IpcEvent?>(null);
            }

            lock (_calls)
            {
                _calls.Add((command, caller));
            }

            return Task.FromResult<IpcEvent?>(Answer);
        }
    }

    /// <summary>
    /// Accepts, slowly, and says when it was reached.
    ///
    /// Stands in for WinVerifyTrust on a cold file, which is where the real UI spends its handshake.
    /// </summary>
    private sealed class SlowVerifier(TimeSpan takes) : IClientVerifier
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default)
        {
            Reached.TrySetResult();
            await Task.Delay(takes, ct);
            return null;
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

        public Task RecordAsync(string type, string? sessionId = null, long? count = null, string? detail = null, CancellationToken ct = default)
        {
            lock (_types)
            {
                _types.Add(type);
            }

            return Task.CompletedTask;
        }
    }
}
