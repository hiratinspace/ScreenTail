using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using ScreenTail.Core.Audit;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// The service's end of the pipe (docs/ipc-contract.md). Accepts any number of clients, runs the handshake
/// on each, answers commands through <see cref="IIpcCommandHandler"/>, and broadcasts events to every
/// authenticated client. Rejections are audit-logged with the reason only.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly Func<NamedPipeServerStream> _pipeFactory;
    private readonly byte[] _token;
    private readonly IClientVerifier _verifier;
    private readonly IIpcCommandHandler _handler;
    private readonly IAuditLog _audit;
    private readonly string _serviceVersion;
    private readonly ConcurrentDictionary<Guid, Connection> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, (long At, long Count)> _rejections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Handshake> _handshakes = new();
    private long _nextHandshake;
    private Task? _acceptLoop;

    public IpcServer(
        Func<NamedPipeServerStream> pipeFactory,
        byte[] token,
        IClientVerifier verifier,
        IIpcCommandHandler handler,
        IAuditLog audit,
        string serviceVersion)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length != IpcToken.Length)
        {
            throw new ArgumentException($"The token must be {IpcToken.Length} bytes.", nameof(token));
        }

        _pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
        _token = (byte[])token.Clone();
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _serviceVersion = serviceVersion ?? throw new ArgumentNullException(nameof(serviceVersion));
    }

    /// <summary>A plain pipe with the platform's default permissions. The Windows service uses an ACL'd factory instead.</summary>
    public static Func<NamedPipeServerStream> DefaultPipeFactory(string pipeName) => () => new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    public int ConnectedClients => _clients.Count;

    /// <summary>Connections turned away before they said anything useful. Counts only (INV-10).</summary>
    public long RefusedConnections { get; private set; }

    /// <summary>
    /// How long a peer has to complete the handshake. A real client writes <c>hello</c> in the same
    /// breath as connecting; two seconds is generous for a local pipe and short enough that holding one
    /// open costs an attacker something.
    /// </summary>
    internal static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The handshake deadline this server applies. Settable only by a test, which may need it short to
    /// watch an authenticated client outlive it, or long so that a busy runner cannot reap the peers a
    /// test is holding before the test has made its point. The service never changes it.
    /// </summary>
    internal TimeSpan HandshakeTimeout { get; init; } = DefaultHandshakeTimeout;

    /// <summary>One row per reason per minute, however fast the rejections arrive.</summary>
    private static readonly TimeSpan RejectionWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Handshakes allowed in flight at once. The real UI opens one connection, the HUD a second, the
    /// diagnostics panel a third; anything beyond a handful at the same instant is not a technician.
    /// </summary>
    private const int MaxHandshakesInFlight = 16;

    /// <summary>
    /// Commands one connection may have waiting behind the one its worker is running (E-25). One more
    /// is refused at once with a failed result, rather than read later: a reader waiting for room is a
    /// reader not hearing the indicator. The UI sends one at a time apart from the ticket picker's
    /// search, so sixteen is room to spare, and at the 1 MiB frame limit it is a bound on memory too.
    /// </summary>
    public const int MostQueuedPerConnection = 16;

    /// <summary>What a command that did not fit in the queue is told. No content, like every refusal here.</summary>
    private const string BusyError = "The capture service is busy with earlier requests. Try again in a moment.";

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Sends an event to every authenticated client. A client that fails to receive it is dropped by its own loop.</summary>
    public async Task BroadcastAsync(IpcEvent ipcEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ipcEvent);

        // All at once, and with a deadline each. One at a time meant a single client that had stopped
        // reading held up every state change to every other window — and the pipe has no output buffer,
        // so "stopped reading" blocks the write rather than filling a queue (2026-09-19 review).
        await Task.WhenAll(_clients.Values.Select(client => TellAsync(client, ipcEvent, ct))).ConfigureAwait(false);
    }

    private static async Task TellAsync(Connection client, IpcEvent ipcEvent, CancellationToken ct)
    {
        using var telling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        telling.CancelAfter(BroadcastTimeout);
        try
        {
            await client.SendAsync(ipcEvent, telling.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Broken, gone, or not reading. Its own loop notices and removes it; a state change nobody
            // could receive is not a reason to hold up the ones who could.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
        Array.Clear(_token);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // Inside the try. CreateNamedPipe runs eagerly here and throws on resource failure, and
                // this loop is an unobserved Task.Run: an escape stopped it for good, silently, leaving
                // the service unable to accept the UI ever again — no HUD, and no way to stop capture
                // from the UI. Backing off and retrying is the only safe answer, because the cause is
                // usually the handle pressure that this loop's own connections created.
                pipe = _pipeFactory();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RefusedConnections++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // A peer that connects and says nothing used to be held for ever, and there was no limit on
            // how many of them. The limit that replaced it refused the newcomer once the table was full,
            // which handed the same attack a better outcome: sixteen silent connections from one process
            // kept the table full, every refusal fell on whoever asked next, and the real UI was the one
            // turned away (weaknesses P1-6). It could then neither see nor change capture state, which
            // defeats INV-4 by making the indicator unreachable rather than by touching capture.
            //
            // So the newcomer is never refused. When the table is full the *oldest silent* handshake is
            // dropped to make room, which is the one least likely to be a real client: a real client
            // writes hello in the same breath as connecting, and is never the one that has been sitting
            // there longest saying nothing.
            var id = Interlocked.Increment(ref _nextHandshake);
            var handshake = new Handshake(_stopping.Token);
            _handshakes[id] = handshake;
            EvictOldestSilent();

            _ = HandleConnectionAsync(pipe, id, handshake);
        }
    }

    /// <summary>
    /// Drops the oldest handshakes that have not spoken yet, until the table is back within its limit.
    ///
    /// Only ever silent ones: an entry is removed from the table the moment its peer says hello, so
    /// anything still here has sent nothing. Cancelling its token ends its read, which closes its pipe.
    /// </summary>
    private void EvictOldestSilent()
    {
        while (_handshakes.Count > MaxHandshakesInFlight)
        {
            var oldest = _handshakes.OrderBy(entry => entry.Key).FirstOrDefault();
            if (oldest.Value is null || !_handshakes.TryRemove(oldest.Key, out var evicted))
            {
                return;
            }

            RefusedConnections++;
            evicted.Cancel();
        }
    }

    /// <summary>
    /// How long the service will spend telling a peer why it was refused.
    ///
    /// Short: the message is a courtesy, and a peer that will not read it is exactly the peer this
    /// deadline exists for.
    /// </summary>
    private static readonly TimeSpan RejectionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long one client may take to accept a state change before it is left behind.</summary>
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(5);

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, long id, Handshake handshake)
    {
        var connection = new Connection(pipe);
        var counted = true;
        try
        {
            // A handshake has to arrive promptly. Without a deadline, a peer that connects and sends
            // nothing holds a pipe instance and a task until the service stops.
            handshake.Deadline(HandshakeTimeout);

            string? reason;
            try
            {
                reason = await HandshakeAsync(
                    connection,
                    () =>
                    {
                        _ = _handshakes.TryRemove(id, out _);
                        counted = false;
                    },
                    handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
                RefusedConnections++;
                return;
            }

            if (reason is not null)
            {
                // Bounded, because the pipe has no output buffer: the write does not return until the
                // peer reads it. A peer that never reads used to hold that pipe instance until the
                // service stopped, and 255 of them locked the UI out for good (2026-09-19 review).
                using var saying = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                saying.CancelAfter(RejectionTimeout);
                try
                {
                    await connection.SendAsync(new Rejected { Reason = reason }, saying.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                {
                    // It would not listen to why. The audit row below is the record either way.
                }

                await AuditRejectionAsync(reason).ConfigureAwait(false);
                return;
            }

            _clients[connection.Id] = connection;
            await ServeAsync(connection, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or IpcProtocolException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or misbehaved; either way this connection is over.
        }
        finally
        {
            if (counted)
            {
                _ = _handshakes.TryRemove(id, out _);
            }

            handshake.Dispose();
            _clients.TryRemove(connection.Id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records that connections were refused, without letting an unauthenticated peer write to the store
    /// as fast as it can connect.
    ///
    /// Every rejection used to INSERT a row. A same-user process looping a bad token at a few thousand a
    /// second had an unbounded write primitive into the encrypted database — audit rows are never pruned,
    /// so it grew until the disk filled, and every real store write starved behind the gate it held. The
    /// rejections are counted in memory and one row is written per minute per reason, which is what an
    /// audit of "somebody is trying" actually needs: that it happened, and how much.
    /// </summary>
    private async Task AuditRejectionAsync(string reason)
    {
        long since;
        lock (_rejections)
        {
            var seen = _rejections.TryGetValue(reason, out var previous);

            // Inside the window since the last row for this reason: count it and write nothing. The test
            // that caught the first version of this got 24 rows from 25 attempts, because resetting the
            // counter after each write made every rejection look like the first one in its window.
            if (seen && Stopwatch.GetElapsedTime(previous.At) < RejectionWindow)
            {
                _rejections[reason] = (previous.At, previous.Count + 1);
                return;
            }

            // First of a window, or the window has passed. The row carries everything since the last one.
            since = (seen ? previous.Count : 0) + 1;
            _rejections[reason] = (Stopwatch.GetTimestamp(), 0);
        }

        await _audit.RecordAsync("ipc_rejected_" + reason, count: since, ct: _stopping.Token).ConfigureAwait(false);
    }

    /// <param name="spoke">
    /// Called the moment a hello has been read, so the eviction table stops counting this connection as
    /// silent.
    ///
    /// It used to be called after the whole handshake, which includes verifying the peer's Authenticode
    /// signature — two WinVerifyTrust calls that take real time on a cold file. So the genuine UI was
    /// "silent" during its own verification and could be evicted by a peer opening connections in a
    /// loop, which is the lockout this table exists to prevent (2026-09-19 review).
    /// </param>
    private async Task<string?> HandshakeAsync(Connection connection, Action spoke, CancellationToken ct)
    {
        IpcCommand? first;
        try
        {
            first = await connection.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (IpcProtocolException)
        {
            return RejectReasons.Protocol;
        }

        if (first is not HelloCommand hello)
        {
            return RejectReasons.Protocol;
        }

        // Said something, whatever it turns out to be worth. Everything below can take its time.
        spoke();

        if (hello.ContractVersion != IpcContract.Version)
        {
            return RejectReasons.ContractMismatch;
        }

        if (!IpcToken.Matches(_token, hello.Token))
        {
            return RejectReasons.BadToken;
        }

        if (await _verifier.VerifyAsync(connection.Pipe, ct).ConfigureAwait(false) is not null)
        {
            return RejectReasons.UnverifiedClient;
        }

        await connection.SendAsync(
            new HelloAck { ContractVersion = IpcContract.Version, ServiceVersion = _serviceVersion, State = _handler.CurrentState },
            ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Reads one connection's commands and answers them, on two lanes (E-25, 2026-10-12 review).
    ///
    /// It used to answer every command before reading the next. The UI has one connection, and the HUD's
    /// two-second "the pill is on screen" shares it with publishing, the ticket search and the
    /// integration screens, which wait on the backend. One slow backend call held the heartbeat past
    /// the indicator's grace, and the service then dropped frames and typing as unindicated with the
    /// pill in plain view (INV-4, INV-6). Stop and Pause waited behind the same call.
    ///
    /// Now the session's own commands (<see cref="AnsweredOnTheReadLoop"/>) are answered here, in the
    /// order they arrive, and never wait for anything but the machine. Everything else goes to one
    /// worker per connection, which runs them in the order they were sent, one at a time, exactly as
    /// before. Replies share the connection's write lock and carry their request id, which the client
    /// already matched on.
    /// </summary>
    private async Task ServeAsync(Connection connection, CancellationToken ct)
    {
        var queue = Channel.CreateBounded<IpcCommand>(new BoundedChannelOptions(MostQueuedPerConnection)
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // Ends the reading on either side: the worker's when the client has gone (what is still queued
        // is dropped, not run blind), and this loop's when the worker failed.
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var worker = Task.Run(() => WorkAsync(connection, queue.Reader, serving, ct), CancellationToken.None);
        try
        {
            while (true)
            {
                var command = await connection.ReadAsync(serving.Token).ConfigureAwait(false);
                if (command is null)
                {
                    return;
                }

                if (AnsweredOnTheReadLoop(command))
                {
                    await ServeOneAsync(connection, command, ct).ConfigureAwait(false);
                }
                else if (!queue.Writer.TryWrite(command))
                {
                    await connection.SendAsync(
                        new CommandResult { RequestId = command.RequestId, Ok = false, Error = BusyError },
                        ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            await serving.CancelAsync().ConfigureAwait(false);

            // Nothing reads this pipe any more, so it is closed now rather than once a slow handler
            // returns. Left open, the client still looked connected and did not reconnect, and the
            // heartbeat, Stop and Pause hung for as long as the backend took (round 1 review of E-25).
            connection.Close();

            // Before the connection is disposed under it. A handler already running finishes, as it
            // always did; its reply has nowhere to go, and that failure is what ends the worker.
            await worker.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The commands that act on, or report, the session being recorded. They are answered from local
    /// state alone, so nothing on the network can hold them up.
    ///
    /// Named here rather than the slow ones, so that a command added later queues by default and can
    /// never be the thing a heartbeat waits behind. <c>request_confirmation</c> is here because a discard
    /// needs its token first. The two settings writes are here so that a Start sent after a Save, which
    /// the tray and the hotkey do without waiting, still records under what was saved; both are a local
    /// file and an in-memory apply.
    /// </summary>
    private static bool AnsweredOnTheReadLoop(IpcCommand command) => command is IndicatorShowingCommand
        or StartCommand or PauseCommand or ResumeCommand or StopCommand or DiscardCommand or MarkMomentCommand
        or RequestConfirmationCommand or GetStateCommand or GetCapabilitiesCommand or HelloCommand
        or SetSettingsCommand or SetCaptureSettingsCommand;

    private async Task WorkAsync(Connection connection, ChannelReader<IpcCommand> queue, CancellationTokenSource serving, CancellationToken ct)
    {
        try
        {
            while (await queue.WaitToReadAsync(serving.Token).ConfigureAwait(false))
            {
                while (!serving.IsCancellationRequested && queue.TryRead(out var command))
                {
                    await ServeOneAsync(connection, command, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (serving.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The client has gone. Whatever it still had queued has nobody to answer to.
        }
        catch
        {
            // A failed handler ended the connection when it ran on the read loop, and the client
            // reconnected. It still does: a reader that went on reading would leave every later command
            // unanswered.
            await serving.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Answers one command, on whichever lane it came in on. A reply too large for one frame goes through
    /// <see cref="AnswerAsync"/> on both lanes, so it is a failed result there too, never a dropped pipe.
    /// </summary>
    private async Task ServeOneAsync(Connection connection, IpcCommand command, CancellationToken ct)
    {
        CommandResult result;
        switch (command)
        {
            case HelloCommand:
                result = new CommandResult { RequestId = command.RequestId, Ok = false, Error = "Already authenticated." };
                break;
            case GetStateCommand:
                await connection.SendAsync(new StateChanged { State = _handler.CurrentState }, ct).ConfigureAwait(false);
                result = new CommandResult { RequestId = command.RequestId, Ok = true };
                break;

            case GetCapabilitiesCommand:
                await connection.SendAsync(
                    _handler.CurrentCapabilities with { RequestId = command.RequestId },
                    ct).ConfigureAwait(false);
                result = new CommandResult { RequestId = command.RequestId, Ok = true };
                break;
            default:
                // A question is answered with its own event first and a result after, so a client
                // that only understands results still learns the command was accepted.
                if (await _handler.ReplyToAsync(command, connection.Id, ct).ConfigureAwait(false) is { } reply)
                {
                    result = await AnswerAsync(connection, reply with { RequestId = command.RequestId }, ct).ConfigureAwait(false);
                }
                else
                {
                    result = await _handler.HandleAsync(command, connection.Id, ct).ConfigureAwait(false);
                }

                break;
        }

        await connection.SendAsync(result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an answer, or refuses with a reason when it would not fit in one frame.
    ///
    /// An answer over the limit used to throw out of the serve loop, which dropped the UI's only
    /// connection: the one carrying the indicator heartbeat and every button (2026-10-12 review, E-2).
    /// The framing refuses before writing a byte, so the pipe is still in step and the result can follow.
    /// </summary>
    private static async Task<CommandResult> AnswerAsync(Connection connection, IpcEvent reply, CancellationToken ct)
    {
        try
        {
            await connection.SendAsync(reply, ct).ConfigureAwait(false);
            return new CommandResult { RequestId = reply.RequestId, Ok = true };
        }
        catch (IpcMessageTooLargeException tooLarge)
        {
            return new CommandResult
            {
                RequestId = reply.RequestId,
                Ok = false,
                Error = $"The answer is too large for the pipe: {tooLarge.Bytes} bytes against a limit of {IpcFraming.MaxMessageBytes}.",
            };
        }
    }

    /// <summary>
    /// One handshake in flight: a token that ends it, whether because the deadline passed, the service is
    /// stopping, or the table filled up and this was the oldest peer that had not spoken.
    /// </summary>
    private sealed class Handshake(CancellationToken stopping) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        public CancellationToken Token => _cts.Token;

        public void Deadline(TimeSpan within) => _cts.CancelAfter(within);

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The handshake finished between being chosen for eviction and being cancelled.
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    private sealed class Connection(NamedPipeServerStream pipe) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Guid Id { get; } = Guid.NewGuid();

        public NamedPipeServerStream Pipe => pipe;

        public Task<IpcCommand?> ReadAsync(CancellationToken ct) => IpcFraming.ReadAsync<IpcCommand>(pipe, ct);

        public async Task SendAsync(IpcEvent ipcEvent, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(pipe, ipcEvent, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Ends the pipe, so the client sees it close, but keeps the write lock: a handler still running
        /// may yet try to answer, and should fail on the pipe rather than on the lock.
        /// </summary>
        public void Close() => pipe.Dispose();

        public async ValueTask DisposeAsync()
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _writeLock.Dispose();
        }
    }
}
