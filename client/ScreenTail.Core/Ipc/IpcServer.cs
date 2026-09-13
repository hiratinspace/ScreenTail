using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
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
    private int _handshaking;
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
    /// How long a peer has to complete the handshake. Generous for a local pipe, and short enough that
    /// holding one open costs an attacker something.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>One row per reason per minute, however fast the rejections arrive.</summary>
    private static readonly TimeSpan RejectionWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Handshakes allowed in flight at once. The real UI opens one connection, the HUD a second, the
    /// diagnostics panel a third; anything beyond a handful at the same instant is not a technician.
    /// </summary>
    private const int MaxHandshakesInFlight = 16;

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
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.SendAsync(ipcEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The connection loop notices the broken pipe and removes the client.
            }
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
            // how many of them. A same-user process could open thousands, park a task and a pipe handle
            // for each, and starve the service out of the handles it needs to accept the real UI.
            if (Interlocked.Increment(ref _handshaking) > MaxHandshakesInFlight)
            {
                _ = Interlocked.Decrement(ref _handshaking);
                RefusedConnections++;
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = HandleConnectionAsync(pipe);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        var connection = new Connection(pipe);
        var counted = true;
        try
        {
            // A handshake has to arrive promptly. Without a deadline, a peer that connects and sends
            // nothing holds a pipe instance and a task until the service stops.
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            handshake.CancelAfter(HandshakeTimeout);

            string? reason;
            try
            {
                reason = await HandshakeAsync(connection, handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
            {
                RefusedConnections++;
                return;
            }

            if (reason is not null)
            {
                await connection.SendAsync(new Rejected { Reason = reason }, _stopping.Token).ConfigureAwait(false);
                await AuditRejectionAsync(reason).ConfigureAwait(false);
                return;
            }

            _ = Interlocked.Decrement(ref _handshaking);
            counted = false;

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
                _ = Interlocked.Decrement(ref _handshaking);
            }

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

    private async Task<string?> HandshakeAsync(Connection connection, CancellationToken ct)
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

    private async Task ServeAsync(Connection connection, CancellationToken ct)
    {
        while (true)
        {
            var command = await connection.ReadAsync(ct).ConfigureAwait(false);
            if (command is null)
            {
                return;
            }

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
                    result = await _handler.HandleAsync(command, ct).ConfigureAwait(false);
                    break;
            }

            await connection.SendAsync(result, ct).ConfigureAwait(false);
        }
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

        public async ValueTask DisposeAsync()
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _writeLock.Dispose();
        }
    }
}
