using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Service.Host;

/// <summary>Maps pipe commands onto the session state machine (ST-020) and reports its state.</summary>
internal sealed class CaptureController(SessionMachine machine, ICapabilityProbe capabilities) : IIpcCommandHandler
{
    public CaptureStateSnapshot CurrentState => machine.Snapshot;

    // Probed on each request rather than cached: a technician can revoke microphone access mid-session.
    public CapabilitiesReported CurrentCapabilities => capabilities.Probe().ToWire();

    public async Task<CommandResult> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var accepted = command switch
        {
            // Manual start (Ctrl+Alt+R, tray). ST-023's detector starts sessions with the real tool.
            StartCommand => await machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: ct).ConfigureAwait(false),
            PauseCommand => await machine.PauseAsync(ct).ConfigureAwait(false),
            ResumeCommand => await machine.ResumeAsync(ct).ConfigureAwait(false),
            StopCommand => await machine.StopAsync(ct).ConfigureAwait(false),
            DiscardCommand => await machine.DiscardAsync(ct).ConfigureAwait(false),
            MarkMomentCommand => await machine.MarkMomentAsync(ct).ConfigureAwait(false),
            _ => false,
        };

        return new CommandResult
        {
            RequestId = command.RequestId,
            Ok = accepted,
            Error = accepted ? null : $"Not possible while {machine.State.ToWire()}.",
        };
    }
}
