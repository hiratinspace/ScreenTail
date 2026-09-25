using System.Collections.Concurrent;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Net;

/// <summary>
/// The activation step over the pipe (ST-010, Spec §5 S8 step 2), and the question the shell asks to
/// show the device's standing. Same two entry points as the other command classes: a question answered
/// with its event, and the reason kept by request id for when there was no answer.
/// </summary>
public sealed class DeviceCommands(DeviceSession session)
{
    private readonly DeviceSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly ConcurrentDictionary<int, string> _refusals = new();

    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case GetDeviceCommand:
                return Report(command.RequestId);

            case ActivateDeviceCommand activate:
                {
                    var answer = await _session.ActivateAsync(activate.Code, activate.DeviceName, ct).ConfigureAwait(false);
                    if (answer.Ok)
                    {
                        return new DeviceActivated { RequestId = command.RequestId, TenantName = answer.Value!.TenantName, DeviceId = answer.Value.DeviceId };
                    }

                    _refusals[command.RequestId] = answer.Refusal!;
                    return null;
                }

            default:
                return null;
        }
    }

    public Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (GetDeviceCommand or ActivateDeviceCommand))
        {
            return Task.FromResult<CommandResult?>(null);
        }

        var reason = _refusals.TryRemove(command.RequestId, out var kept) ? kept : "The device could not be activated.";
        return Task.FromResult<CommandResult?>(new CommandResult { RequestId = command.RequestId, Ok = false, Error = reason });
    }

    private DeviceReported Report(int requestId) => new()
    {
        RequestId = requestId,
        Activated = _session.Activated,
        TenantName = _session.TenantName,
        DeviceId = _session.DeviceId,
        DaysOffline = _session.DaysOffline,
        BeyondGrace = _session.BeyondGrace,
        Revoked = _session.Revoked,
        Standing = _session.Standing,
    };
}
