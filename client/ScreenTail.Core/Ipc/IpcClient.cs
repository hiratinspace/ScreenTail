using System.Collections.Concurrent;
using System.IO.Pipes;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// The UI's end of the pipe. <see cref="ConnectAsync"/> completes the handshake or throws
/// <see cref="IpcRejectedException"/>; afterwards commands get their <see cref="CommandResult"/> and
/// events raise <see cref="StateChanged"/>. Holds no capture state of its own: the service does (ADR-0003).
/// </summary>
public sealed class IpcClient : ICaptureChannel
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcEvent>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private int _nextRequestId;
    private Task? _readLoop;
    private volatile bool _disconnected;

    private IpcClient(NamedPipeClientStream pipe, HelloAck ack)
    {
        _pipe = pipe;
        State = ack.State;
        ServiceVersion = ack.ServiceVersion;
    }

    public event Action<CaptureStateSnapshot>? StateChanged;

    public event Action? Disconnected;

    public CaptureStateSnapshot State { get; private set; }

    public string ServiceVersion { get; }

    // PipeStream.IsConnected doesn't notice a peer that went away on every platform; the read loop does.
    public bool IsConnected => !_disconnected && !_closing.IsCancellationRequested;

    /// <exception cref="IpcRejectedException">The service refused the handshake.</exception>
    /// <exception cref="TimeoutException">No service answered on the pipe within <paramref name="timeout"/>.</exception>
    /// <param name="serverVerifier">
    /// Checks that the process answering on the pipe is really the capture service, before the token is
    /// written (ST-012). Required rather than optional, and deliberately so: null is a decision to hand
    /// the session token to whoever answers, which is right for a test and wrong everywhere else. A
    /// default would make forgetting it look identical to choosing it.
    /// </param>
    public static async Task<IpcClient> ConnectAsync(
        string pipeName,
        byte[] token,
        string clientName,
        IServerVerifier? serverVerifier,
        string? clientVersion = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        // Anonymous impersonation: without it a pipe server can impersonate the user who connected to it,
        // and this client runs as the technician. The service needs nothing from our identity — it checks
        // the executable — so there is nothing to give up by refusing.
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Anonymous);
        try
        {
            await pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds, ct).ConfigureAwait(false);

            // Before the token, not after. The token is the thing worth stealing, and a handshake that
            // proves who we are to an unverified stranger has proved it to the stranger.
            if (serverVerifier is not null
                && await serverVerifier.VerifyAsync(pipe, ct).ConfigureAwait(false) is { } refusal)
            {
                throw new IpcUntrustedServerException(refusal);
            }

            await IpcFraming.WriteAsync<IpcCommand>(
                pipe,
                new HelloCommand
                {
                    RequestId = 0,
                    ContractVersion = IpcContract.Version,
                    Token = IpcToken.Encode(token),
                    ClientName = clientName,
                    ClientVersion = clientVersion,
                },
                ct).ConfigureAwait(false);

            var reply = await IpcFraming.ReadAsync<IpcEvent>(pipe, ct).ConfigureAwait(false);
            var ack = reply switch
            {
                HelloAck ok => ok,
                Rejected rejected => throw new IpcRejectedException(rejected.Reason),
                null => throw new IpcProtocolException("The service closed the pipe during the handshake."),
                _ => throw new IpcProtocolException($"Unexpected handshake reply {reply.GetType().Name}."),
            };

            var client = new IpcClient(pipe, ack);
            client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);
            return client;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a command built with a fresh request id and waits for its result.</summary>
    public async Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default)
    {
        var reply = await ExchangeAsync(build, ct).ConfigureAwait(false);
        return reply as CommandResult
            ?? new CommandResult { RequestId = reply.RequestId, Ok = false, Error = $"Unexpected reply {reply.GetType().Name}." };
    }

    /// <summary>
    /// Sends a command whose answer is an event of its own rather than a bare result, and waits for it
    /// (<c>get_capabilities</c>, <c>get_diagnostics</c>, <c>list_sessions</c>).
    ///
    /// The service answers a failure with a <see cref="CommandResult"/> even for these, so a refusal
    /// arrives as an exception carrying the service's own words rather than as a null the caller has to
    /// remember to check.
    /// </summary>
    /// <exception cref="IpcProtocolException">The service refused, or answered with something else.</exception>
    public async Task<TReply> RequestAsync<TReply>(Func<int, IpcCommand> build, CancellationToken ct = default)
        where TReply : IpcEvent
    {
        var reply = await ExchangeAsync(build, ct).ConfigureAwait(false);
        return reply switch
        {
            TReply typed => typed,
            CommandResult { Ok: false } failed => throw new IpcProtocolException(failed.Error ?? "The capture service refused the request."),
            _ => throw new IpcProtocolException($"Expected {typeof(TReply).Name}, got {reply.GetType().Name}."),
        };
    }

    private async Task<IpcEvent> ExchangeAsync(Func<int, IpcCommand> build, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var command = build(requestId);
        if (command.RequestId != requestId)
        {
            throw new ArgumentException("The command must carry the request id it was given.", nameof(build));
        }

        var completion = new TaskCompletionSource<IpcEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(_pipe, command, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            await using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }

        _writeLock.Dispose();
        _closing.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var ipcEvent = await IpcFraming.ReadAsync<IpcEvent>(_pipe, _closing.Token).ConfigureAwait(false);
                if (ipcEvent is null)
                {
                    break;
                }

                // A state change is always volunteered; everything else that carries a request id is an
                // answer to one, whatever its type. One rule rather than a case per reply type, which is
                // what let get_capabilities ship with a reply nothing could ever receive.
                if (ipcEvent is StateChanged changed)
                {
                    State = changed.State;
                    StateChanged?.Invoke(changed.State);
                }
                else if (ipcEvent.RequestId is { } id && _pending.TryGetValue(id, out var completion))
                {
                    completion.TrySetResult(ipcEvent);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or IpcProtocolException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _disconnected = true;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new IOException("The capture service disconnected."));
        }

        if (!_closing.IsCancellationRequested)
        {
            Disconnected?.Invoke();
        }
    }
}

/// <summary>
/// Something answered on the pipe and it was not the capture service (ST-012). Distinct from
/// <see cref="IpcRejectedException"/>, which is the service refusing us: this is us refusing it, and the
/// shell says so rather than offering to start a service that is evidently already running.
/// </summary>
public sealed class IpcUntrustedServerException : Exception
{
    public IpcUntrustedServerException()
        : this(RejectReasons.Protocol)
    {
    }

    public IpcUntrustedServerException(string reason)
        : base($"The process answering on the capture pipe is not the capture service: {reason}.")
    {
        Reason = reason;
    }

    public IpcUntrustedServerException(string message, Exception inner)
        : base(message, inner)
    {
        Reason = RejectReasons.Protocol;
    }

    public string Reason { get; }
}

public sealed class IpcRejectedException : Exception
{
    public IpcRejectedException()
        : this(RejectReasons.Protocol)
    {
    }

    public IpcRejectedException(string reason)
        : base($"The capture service rejected the connection: {reason}.")
    {
        Reason = reason;
    }

    public IpcRejectedException(string message, Exception inner)
        : base(message, inner)
    {
        Reason = RejectReasons.Protocol;
    }

    public string Reason { get; }
}
