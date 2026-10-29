using ScreenTail.Core.Ipc;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-085. The thing that was missing between a UI full of working screens and a service full of real
/// state: something that owns the pipe, keeps trying, and tells the shell the truth in between.
///
/// The rule every test here turns on is that <em>unknown is not idle</em>. When the pipe drops, the
/// session did not stop; the UI simply stopped being able to see it. Anything that reports "not
/// recording" in that moment is the silent-capture path INV-4 forbids, so the connection keeps the last
/// state it was told and marks itself unavailable beside it.
/// </summary>
public sealed class CaptureConnectionTests
{
    private static readonly CaptureStateSnapshot Recording = new()
    {
        State = CaptureStates.Recording,
        SessionId = "s1",
        RemoteTool = "screenconnect",
        ElapsedMs = 61_000,
    };

    [Fact]
    public async Task TheFirstPaintIsTheStateTheHandshakeReturned()
    {
        var shell = new ShellState();
        var channel = new FakeChannel(Recording);
        await using var connection = Connect(shell, () => channel);

        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        Assert.Equal(ServiceConnection.Connected, shell.Snapshot.Connection);
        Assert.Equal(CaptureStates.Recording, shell.Snapshot.Capture!.State);
        Assert.Null(shell.Snapshot.Banner);
    }

    [Fact]
    public async Task AStateChangeFromTheServiceReachesTheShell()
    {
        var shell = new ShellState();
        var channel = new FakeChannel(Recording);
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        channel.Raise(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1" });

        await WaitFor(() => shell.Snapshot.Capture!.State == CaptureStates.Paused);
    }

    [Fact]
    public async Task ADroppedPipeIsUnavailableAndKeepsWhatTheServiceLastSaid()
    {
        // The heart of it. A technician whose pipe drops mid-session must not be shown "not recording":
        // the recording is still running, and the UI has merely gone blind. It says so instead.
        var shell = new ShellState();
        var channel = new FakeChannel(Recording);
        await using var connection = Connect(shell, () => channel, reconnect: false);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        channel.Drop();

        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Unavailable);
        Assert.Equal(CaptureStates.Recording, shell.Snapshot.Capture!.State);
        Assert.Equal("Capture service not running. Start it to capture sessions.", shell.Snapshot.Banner);
    }

    [Fact]
    public async Task ADroppedConnectionIsReopenedAndTheOldChannelIsLetGo()
    {
        // T-78. A service that restarts mid-day drops every connection it had. The UI must open a new
        // one by itself, and the old channel must be finished with: disposed, and no longer able to
        // speak for the service, so a late event or a second drop from it cannot move the shell or
        // start another reconnect.
        var shell = new ShellState();
        var first = new FakeChannel(Recording);
        var second = new FakeChannel(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1" });
        var opened = 0;
        await using var connection = Connect(shell, () => ++opened == 1 ? first : second);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        first.Drop();

        await WaitFor(() => opened == 2 && shell.Snapshot.Capture?.State == CaptureStates.Paused);
        Assert.Equal(ServiceConnection.Connected, shell.Snapshot.Connection);
        Assert.True(first.Disposed, "the dropped channel was not disposed");
        Assert.False(second.Disposed);

        // The old channel has nothing more to say. Neither a stale event nor a second drop counts.
        first.Raise(new CaptureStateSnapshot { State = CaptureStates.Idle });
        first.Drop();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStates.Paused, shell.Snapshot.Capture!.State);
        Assert.Equal(ServiceConnection.Connected, shell.Snapshot.Connection);
        Assert.Equal(2, opened);
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task AServiceThatIsNotThereYetIsUnavailableRatherThanAnError()
    {
        var shell = new ShellState();
        await using var connection = Connect(shell, () => throw new TimeoutException("nothing is listening"));

        await connection.StartAsync(TestContext.Current.CancellationToken);

        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Unavailable);
        Assert.Null(shell.Snapshot.Capture);
    }

    [Fact]
    public async Task ItKeepsTryingAndRecoversWithoutTheUiRestarting()
    {
        // The service restarting mid-day is ordinary, and the UI has to survive it by itself. A
        // technician who has to close and reopen the app to see capture state again will stop believing
        // the app.
        var shell = new ShellState();
        var attempts = 0;
        var channel = new FakeChannel(Recording);
        await using var connection = Connect(
            shell,
            () => ++attempts == 1 ? throw new TimeoutException("not up yet") : channel);

        await connection.StartAsync(TestContext.Current.CancellationToken);

        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);
        Assert.True(attempts >= 2);
        Assert.Equal(CaptureStates.Recording, shell.Snapshot.Capture!.State);
    }

    [Fact]
    public async Task BuiltTheWayTheApplicationBuildsItItStillRetries()
    {
        // The test that was missing. Every other case here hands the connection a retry schedule, so
        // none of them noticed that the application handed it nothing — and that "nothing" meant
        // "never", not "the default" as the parameter's own documentation claimed. A UI that started a
        // moment before the service gave up after one attempt and showed a grey tray icon until someone
        // restarted it, while the service went on starting sessions by itself (INV-4; 2026-09-19 review).
        //
        // Two arguments, exactly as LiveShell passes them.
        var shell = new ShellState();
        var attempts = 0;
        await using var connection = new CaptureConnection(
            shell,
            _ =>
            {
                attempts++;
                return attempts < 2
                    ? throw new TimeoutException("nothing is listening yet")
                    : Task.FromResult<ICaptureChannel>(new FakeChannel(Recording));
            });

        await connection.StartAsync(TestContext.Current.CancellationToken);

        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);
        Assert.Equal(ServiceConnection.Connected, shell.Snapshot.Connection);
    }

    [Fact]
    public async Task AServiceThatRestartedIsReadItsNewTokenRatherThanGivenUpOn()
    {
        // The token is rotated on every service start and the UI reads it from a file just before it
        // opens the pipe. Read the file, wait for the pipe, and a service that came up in between has
        // already replaced the token: the refusal is "bad_token", and it is a race rather than an
        // answer. Trying again re-reads the file. Bounded, because a token that is wrong three times
        // running is not a race any more.
        var shell = new ShellState();
        var attempts = 0;
        await using var connection = Connect(
            shell,
            () =>
            {
                attempts++;
                return attempts < 2
                    ? throw new IpcRejectedException(RejectReasons.BadToken)
                    : new FakeChannel(Recording);
            });

        await connection.StartAsync(TestContext.Current.CancellationToken);

        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);
        Assert.Equal(ServiceConnection.Connected, shell.Snapshot.Connection);
        Assert.Null(connection.LastRefusal);
    }

    [Fact]
    public async Task ATokenThatKeepsBeingWrongStopsBeingRetried()
    {
        var shell = new ShellState();
        var attempts = 0;
        await using var connection = Connect(
            shell,
            () =>
            {
                attempts++;
                throw new IpcRejectedException(RejectReasons.BadToken);
            });

        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => connection.LastRefusal is not null);

        Assert.Equal(CaptureConnection.StaleTokenAttempts, attempts);
    }

    [Fact]
    public async Task ARefusedHandshakeIsNotRetriedIntoTheGround()
    {
        // Being told "you are not who you say you are" is not a transient failure, and hammering the
        // pipe with a token the service has rejected writes an audit row every time.
        var shell = new ShellState();
        var attempts = 0;
        await using var connection = Connect(
            shell,
            () =>
            {
                attempts++;
                throw new IpcRejectedException(RejectReasons.BadToken);
            });

        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => connection.LastRefusal is not null);

        Assert.Equal(RejectReasons.BadToken, connection.LastRefusal);
        Assert.Equal(ServiceConnection.Unavailable, shell.Snapshot.Connection);
        var settled = attempts;
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Assert.Equal(settled, attempts);
    }

    [Fact]
    public async Task AnImpostorOnThePipeIsRefusedAndSaidSo()
    {
        // ST-012's other half. Something answering on the pipe that is not the service must not be
        // retried past: it would receive the session token on the next attempt.
        var shell = new ShellState();
        await using var connection = Connect(
            shell,
            () => throw new IpcUntrustedServerException(RejectReasons.UnverifiedClient));

        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => connection.LastRefusal is not null);

        Assert.Equal(RejectReasons.UnverifiedClient, connection.LastRefusal);
        Assert.Equal(ServiceConnection.Unavailable, shell.Snapshot.Connection);
    }

    [Fact]
    public async Task ACommandWithNoConnectionFailsInsteadOfThrowing()
    {
        var shell = new ShellState();
        await using var connection = Connect(shell, () => throw new TimeoutException("nothing is listening"));
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var result = await connection.SendAsync(id => new PauseCommand { RequestId = id }, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("Not connected to the capture service.", result.Error);
    }

    [Fact]
    public async Task ACommandReachesTheService()
    {
        var shell = new ShellState();
        var channel = new FakeChannel(Recording);
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        var result = await connection.SendAsync(id => new PauseCommand { RequestId = id }, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Single(channel.Sent);
        Assert.IsType<PauseCommand>(channel.Sent[0]);
    }

    [Fact]
    public async Task AskingSomethingWithNoConnectionAnswersNullRatherThanThrowing()
    {
        // Every caller is a screen opening. A screen whose load throws takes the window with it, and a
        // technician with no capture service should see a panel saying so, not a crash.
        var shell = new ShellState();
        await using var connection = Connect(shell, () => throw new TimeoutException("nothing is listening"));
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var reply = await connection.RequestAsync<DiagnosticsReported>(
            id => new GetDiagnosticsCommand { RequestId = id },
            TestContext.Current.CancellationToken);

        Assert.Null(reply);
    }

    [Fact]
    public async Task AskingSomethingReachesTheServiceAndComesBackTyped()
    {
        var shell = new ShellState();
        var channel = new FakeChannel(Recording)
        {
            Reply = new SessionsListed { Sessions = [] },
        };
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        var reply = await connection.RequestAsync<SessionsListed>(
            id => new ListSessionsCommand { RequestId = id },
            TestContext.Current.CancellationToken);

        Assert.NotNull(reply);
        Assert.Empty(reply.Sessions);
        Assert.IsType<ListSessionsCommand>(Assert.Single(channel.Sent));
    }

    [Fact]
    public async Task ARefusedQuestionKeepsTheServicesWordsAndIsAskedOnce()
    {
        // S-36: the reason is on the reply that came back. Nothing is sent a second time to learn it,
        // because for publish or activate a second send is a second publish or a second activation.
        var shell = new ShellState();
        var channel = new FakeChannel(Recording)
        {
            Reply = new CommandResult { RequestId = 1, Ok = false, Error = "Connect a PSA to publish." },
        };
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        var answer = await connection.AskAsync<SessionPublished>(
            id => new PublishSessionCommand { RequestId = id, SessionId = "s1", TicketId = "1", NoteType = "internal", Minutes = 0, Destinations = ["ticket_note"], Note = ScreenTail.Tests.Review.ReviewFixture.Draft(), FrameIds = [] },
            TestContext.Current.CancellationToken);

        Assert.Null(answer.Reply);
        Assert.Equal("Connect a PSA to publish.", answer.Refusal);
        Assert.IsType<PublishSessionCommand>(Assert.Single(channel.Sent));
    }

    [Theory]
    [InlineData("typed", null)]
    [InlineData("refused-without-words", "The capture service refused the request.")]
    [InlineData("ok-result", "The capture service answered with CommandResult, not SessionsListed.")]
    [InlineData("other-event", "The capture service answered with IntegrationsListed, not SessionsListed.")]
    [InlineData("broken", "The capture service disconnected.")]
    public async Task AQuestionAlwaysComesBackAsAReplyOrAReason(string reply, string? refusal)
    {
        var shell = new ShellState();
        var channel = new FakeChannel(Recording)
        {
            Reply = reply switch
            {
                "typed" => new SessionsListed { Sessions = [] },
                "refused-without-words" => new CommandResult { RequestId = 1, Ok = false },
                "ok-result" => new CommandResult { RequestId = 1, Ok = true },
                "other-event" => new IntegrationsListed { RequestId = 1, Integrations = [] },
                _ => null,
            },
        };
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        var answer = await connection.AskAsync<SessionsListed>(id => new ListSessionsCommand { RequestId = id }, TestContext.Current.CancellationToken);

        Assert.Equal(refusal, answer.Refusal);
        Assert.Equal(refusal is null, answer.Reply is not null);
        Assert.Single(channel.Sent);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("protocol")]
    public async Task APipeThatBreaksMidQuestionSaysSoInsteadOfThrowing(string fault)
    {
        // A real dropped pipe fails the pending request with IOException (IpcClient's read loop), and a
        // client torn down mid-ask with ObjectDisposedException. Either must come back as words on the
        // Publish button, not as an exception out of it.
        var shell = new ShellState();
        var channel = new FakeChannel(Recording)
        {
            Fault = fault switch
            {
                "io" => new IOException("Pipe is broken."),
                "disposed" => new ObjectDisposedException("IpcClient"),
                _ => new IpcProtocolException("frame too large"),
            },
        };
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        var answer = await connection.AskAsync<SessionPublished>(
            id => new PublishSessionCommand { RequestId = id, SessionId = "s1", TicketId = "1", NoteType = "internal", Minutes = 0, Destinations = ["ticket_note"], Note = ScreenTail.Tests.Review.ReviewFixture.Draft(), FrameIds = [] },
            TestContext.Current.CancellationToken);

        Assert.Null(answer.Reply);
        Assert.Equal("The capture service disconnected.", answer.Refusal);
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task ACancelledQuestionIsCancelledNotRefused()
    {
        // Cancellation is the caller's own doing and keeps propagating, so it is never shown as a refusal.
        var shell = new ShellState();
        var channel = new FakeChannel(Recording) { Fault = new OperationCanceledException() };
        await using var connection = Connect(shell, () => channel);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => shell.Snapshot.Connection == ServiceConnection.Connected);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.AskAsync<SessionsListed>(id => new ListSessionsCommand { RequestId = id }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AQuestionWithNoConnectionSaysSoWithoutSending()
    {
        var shell = new ShellState();
        await using var connection = Connect(shell, () => throw new TimeoutException("nothing is listening"), reconnect: false);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var answer = await connection.AskAsync<DeviceActivated>(id => new ActivateDeviceCommand { RequestId = id, Code = "KX7PM-4R2WQ", DeviceName = "TECH" }, TestContext.Current.CancellationToken);

        Assert.Null(answer.Reply);
        Assert.Equal("Not connected to the capture service.", answer.Refusal);
    }

    [Theory]
    [InlineData(1, 250)]
    [InlineData(2, 500)]
    [InlineData(3, 1000)]
    [InlineData(4, 2000)]
    [InlineData(5, 4000)]
    [InlineData(9, 10000)]
    [InlineData(40, 10000)]
    public void TheRetryScheduleBacksOffAndStops(int attempt, int expectedMs)
    {
        // Quick at first, because a service that is still starting is the common case and the UI should
        // catch up within a second. Capped, because a service that is genuinely gone should not be a
        // background process opening a pipe forever.
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), CaptureConnection.RetryAfter(attempt));
    }

    private static CaptureConnection Connect(
        ShellState shell,
        Func<ICaptureChannel> open,
        bool reconnect = true) =>
        new(
            shell,
            _ => Task.FromResult(open()),
            retryAfter: reconnect ? _ => TimeSpan.FromMilliseconds(5) : CaptureConnection.Never);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), "the connection never reached the expected state");
    }

    private sealed class FakeChannel(CaptureStateSnapshot state) : ICaptureChannel
    {
        public List<IpcCommand> Sent { get; } = [];

        public CaptureStateSnapshot State { get; private set; } = state;

        public bool IsConnected { get; private set; } = true;

        public bool Disposed { get; private set; }

        public event Action<CaptureStateSnapshot>? StateChanged;

        public event Action? Disconnected;

        public void Raise(CaptureStateSnapshot next)
        {
            State = next;
            StateChanged?.Invoke(next);
        }

        public void Drop()
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }

        /// <summary>What a typed request is answered with, when a test sets one.</summary>
        public IpcEvent? Reply { get; set; }

        /// <summary>What a typed request fails with instead, when a test sets one.</summary>
        public Exception? Fault { get; set; }

        public Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default)
        {
            var command = build(1);
            Sent.Add(command);
            return Task.FromResult(new CommandResult { RequestId = command.RequestId, Ok = true });
        }

        public Task<TReply> RequestAsync<TReply>(Func<int, IpcCommand> build, CancellationToken ct = default)
            where TReply : IpcEvent
        {
            Sent.Add(build(1));
            if (Fault is not null)
            {
                return Task.FromException<TReply>(Fault);
            }

            return Reply is TReply typed
                ? Task.FromResult(typed)
                : Task.FromException<TReply>(new IpcProtocolException("no reply set"));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
