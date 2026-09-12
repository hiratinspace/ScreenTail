using System.IO.Pipes;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// Decides whether the process on the other end of a freshly connected pipe may drive capture
/// (ADR-0003: signed by our publisher, or a sibling of the service executable in unsigned dev builds).
/// The Windows implementation resolves the client process from the pipe; tests use fakes.
/// </summary>
public interface IClientVerifier
{
    /// <returns>null when the client is acceptable; otherwise a short reason with no content in it.</returns>
    ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default);
}

/// <summary>What the service does with commands once a client is authenticated. ST-020 supplies the real one.</summary>
public interface IIpcCommandHandler
{
    CaptureStateSnapshot CurrentState { get; }

    /// <summary>What Windows allows right now (ST-021). Re-checked on request: permissions change mid-day.</summary>
    CapabilitiesReported CurrentCapabilities { get; }

    Task<CommandResult> HandleAsync(IpcCommand command, CancellationToken ct = default);
}
