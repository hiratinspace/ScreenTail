using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Update;

/// <summary>
/// The update questions and commands over the pipe (ST-113): where this installation stands, the
/// technician's channel, and Install. Same two entry points as the other command classes.
/// </summary>
public sealed class UpdateCommands(
    UpdateService updates,
    UpdateInstaller installer,
    Func<UpdatePolicy> policy,
    UpdatePreferences preferences,
    Func<bool> sessionBusy,
    Action channelChanged)
{
    public const string ChannelLockedRefusal = "Your administrator chose the update channel, so it cannot be changed here.";

    private readonly UpdateService _updates = updates ?? throw new ArgumentNullException(nameof(updates));
    private readonly UpdateInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    private readonly Func<UpdatePolicy> _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly UpdatePreferences _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    private readonly Func<bool> _sessionBusy = sessionBusy ?? throw new ArgumentNullException(nameof(sessionBusy));
    private readonly Action _channelChanged = channelChanged ?? throw new ArgumentNullException(nameof(channelChanged));

    public Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Task.FromResult<IpcEvent?>(command is GetUpdateCommand ? Report(command.RequestId) : null);
    }

    public async Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case InstallUpdateCommand:
                {
                    var answer = await _installer.InstallAsync(ct).ConfigureAwait(false);
                    return new CommandResult { RequestId = command.RequestId, Ok = answer.Ok, Error = answer.Refusal };
                }

            case SetUpdateChannelCommand set:
                return new CommandResult { RequestId = command.RequestId, Ok = SetChannel(set.Channel, out var refusal), Error = refusal };

            case GetUpdateCommand:
                return new CommandResult { RequestId = command.RequestId, Ok = false, Error = "The update status could not be read." };

            default:
                return null;
        }
    }

    /// <summary>What Settings shows. The channel is the admin's when locked, otherwise the technician's.</summary>
    public UpdateReported Report(int? requestId = null)
    {
        var policy = _policy();
        var status = _updates.Status;
        return new UpdateReported
        {
            RequestId = requestId,
            State = UpdateWire.Name(status.State),
            InstalledVersion = ReleaseVersion.Format(_updates.Installed),
            AvailableVersion = status.AvailableVersion,
            Channel = (policy.Channel ?? _preferences.Load()).ToWire(),
            ChannelLocked = policy.ChannelLocked,
            Sentence = status.Sentence,
            CanInstall = status.State == UpdateState.Ready && !_sessionBusy(),
        };
    }

    private bool SetChannel(string wire, out string? refusal)
    {
        if (_policy().ChannelLocked)
        {
            refusal = ChannelLockedRefusal;
            return false;
        }

        if (!UpdateChannels.TryParse(wire, out var channel))
        {
            refusal = "The update channel is stable or pilot.";
            return false;
        }

        if (!_preferences.Save(channel))
        {
            refusal = "The update channel could not be saved.";
            return false;
        }

        refusal = null;
        _channelChanged();
        return true;
    }
}

/// <summary>The states' names on the wire.</summary>
public static class UpdateWire
{
    public static string Name(UpdateState state) => state switch
    {
        UpdateState.NotConfigured => "not_configured",
        UpdateState.DevelopmentBuild => "development_build",
        UpdateState.Off => "off",
        UpdateState.ChannelUnknown => "channel_unknown",
        UpdateState.Blocked => "blocked",
        UpdateState.Waiting => "waiting",
        UpdateState.Checking => "checking",
        UpdateState.UpToDate => "up_to_date",
        UpdateState.Available => "available",
        UpdateState.Downloading => "downloading",
        UpdateState.Ready => "ready",
        UpdateState.Installing => "installing",
        UpdateState.Refused => "refused",
        _ => "failed",
    };
}
