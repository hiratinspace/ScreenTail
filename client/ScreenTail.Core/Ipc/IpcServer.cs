using System.Collections.Concurrent;
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
            var pipe = _pipeFactory();
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

            _ = HandleConnectionAsync(pipe);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        var connection = new Connection(pipe);
        try
        {
            var reason = await HandshakeAsync(connection, _stopping.Token).ConfigureAwait(false);
            if (reason is not null)
            {
                await connection.SendAsync(new Rejected { Reason = reason }, _stopping.Token).ConfigureAwait(false);
                await _audit.RecordAsync("ipc_rejected_" + reason, ct: _stopping.Token).ConfigureAwait(false);
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
            _clients.TryRemove(connection.Id, out _);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
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
