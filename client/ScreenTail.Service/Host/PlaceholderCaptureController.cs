using ScreenTail.Core.Ipc;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Service.Host;

/// <summary>
/// Answers the pipe until ST-020 supplies the session state machine: always idle, and every capture
/// command is refused with a plain reason. Lets the UI (ST-070) be built against a real service.
/// </summary>
internal sealed class PlaceholderCaptureController : IIpcCommandHandler
{
    public CaptureStateSnapshot CurrentState => CaptureStateSnapshot.Idle;

    public Task<CommandResult> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult(new CommandResult
        {
            RequestId = command.RequestId,
            Ok = false,
            Error = "Capture isn't available yet: the session state machine arrives with ST-020.",
        });
    }
}
