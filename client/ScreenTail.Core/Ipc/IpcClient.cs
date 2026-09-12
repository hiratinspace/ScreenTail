using System.Collections.Concurrent;
using System.IO.Pipes;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// The UI's end of the pipe. <see cref="ConnectAsync"/> completes the handshake or throws
/// <see cref="IpcRejectedException"/>; afterwards commands get their <see cref="CommandResult"/> and
/// events raise <see cref="StateChanged"/>. Holds no capture state of its own: the service does (ADR-0003).
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<CommandResult>> _pending = new();
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
    public static async Task<IpcClient> ConnectAsync(
        string pipeName,
        byte[] token,
        string clientName,
        string? clientVersion = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds, ct).ConfigureAwait(false);
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
        ArgumentNullException.ThrowIfNull(build);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var command = build(requestId);
        if (command.RequestId != requestId)
        {
            throw new ArgumentException("The command must carry the request id it was given.", nameof(build));
        }

        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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

                switch (ipcEvent)
                {
                    case CommandResult result:
                        if (_pending.TryGetValue(result.RequestId, out var completion))
                        {
                            completion.TrySetResult(result);
                        }

                        break;
                    case StateChanged changed:
                        State = changed.State;
                        StateChanged?.Invoke(changed.State);
                        break;
                    default:
                        break;
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
