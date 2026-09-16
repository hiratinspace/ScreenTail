using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// One live connection to the capture service, as everything above the transport sees it (ST-085).
///
/// <see cref="IpcClient"/> is the real one. The interface exists so that the thing which decides
/// <em>when</em> to connect, what to show while it cannot, and when to give up can be argued about and
/// tested without a named pipe, a Windows machine or a running service — which is most of what INV-4
/// turns on.
/// </summary>
public interface ICaptureChannel : IAsyncDisposable
{
    /// <summary>The last state the service reported, starting with the one the handshake returned.</summary>
    CaptureStateSnapshot State { get; }

    bool IsConnected { get; }

    event Action<CaptureStateSnapshot>? StateChanged;

    /// <summary>The service went away. Raised once, and never for a disposal the caller asked for.</summary>
    event Action? Disconnected;

    Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default);

    /// <summary>Sends a command whose answer is an event of its own, and waits for it.</summary>
    Task<TReply> RequestAsync<TReply>(Func<int, IpcCommand> build, CancellationToken ct = default)
        where TReply : IpcEvent;
}
