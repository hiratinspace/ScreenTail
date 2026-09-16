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

    /// <summary>
    /// The reply an asking command wants, or null when the command is an instruction rather than a
    /// question (ST-085).
    ///
    /// Separate from <see cref="HandleAsync"/> because the two answer differently: an instruction gets a
    /// result saying whether it was accepted, while a question gets an event of its own and a result
    /// saying the question was understood. Keeping them apart is what stops <see cref="IpcServer"/>
    /// growing a case per reply type.
    /// </summary>
    Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default) => Task.FromResult<IpcEvent?>(null);
}

/// <summary>
/// The other half of ADR-0003 rule 2, and the half that was missing: the UI deciding whether the process
/// answering on the pipe is really the capture service, <em>before</em> it hands over the session token.
///
/// Without it the handshake is one-sided. Anything that can create the pipe name first — a per-user pipe,
/// so no privilege is needed — receives the token, and can then feed the UI whatever capture state it
/// likes. That breaks INV-4 in the worst direction: a technician is shown "not recording" while recording
/// continues, which is the one lie this product must never tell.
/// </summary>
public interface IServerVerifier
{
    /// <returns>null when the server is really the service; otherwise a short reason with no content in it.</returns>
    ValueTask<string?> VerifyAsync(PipeStream connection, CancellationToken ct = default);
}
