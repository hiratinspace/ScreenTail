using ScreenTail.Core.Shell;
using ScreenTail.Core.Update;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Settings;

/// <param name="Sentence">The service's one line about where this installation stands.</param>
/// <param name="CanInstall">Ready, and no session recording.</param>
public sealed record UpdateSnapshot(string State, string InstalledVersion, string? AvailableVersion, UpdateChannel Channel, bool ChannelLocked, string Sentence, bool CanInstall);

/// <summary>What the Updates card asks the service (ST-113). Over the pipe in the running application; a fake in tests.</summary>
public interface IUpdateGateway
{
    /// <summary>Null when the service did not answer.</summary>
    Task<UpdateSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>Null when the service took it; otherwise its refusal.</summary>
    Task<string?> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default);

    /// <summary>Null when Windows Installer was started; otherwise why not.</summary>
    Task<string?> InstallAsync(CancellationToken ct = default);
}

/// <summary>
/// Settings → Updates (ST-113 AC3): where this installation stands, the channel, and Install.
///
/// The channel is the technician's to choose unless the tenant's admin chose it, in which case it is
/// shown, locked, with who set it (INV-11), the same way Privacy shows a locked local-only. Install is
/// offered only when the service says an update is ready and no session is recording; the service
/// checks both again when it is pressed, so a stale card cannot install mid-session.
/// </summary>
public sealed class UpdatesPanel(IUpdateGateway gateway)
{
    public const string LockedNote = "Set by your administrator";

    public const string WhatInstallDoes =
        "Windows asks for an administrator's approval. ScreenTail closes while it installs and starts again at your next sign-in, or when you open it from the Start menu.";

    private readonly IUpdateGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private bool _busy;

    public string Sentence { get; private set; } = "Asking the capture service…";

    public string InstalledVersion { get; private set; } = string.Empty;

    public UpdateChannel Channel { get; private set; } = UpdateChannel.Stable;

    public bool ChannelLocked { get; private set; }

    /// <summary>Under the channel choice when the admin set it; null otherwise.</summary>
    public string? ChannelNote => ChannelLocked ? LockedNote : null;

    public bool CanChooseChannel => Answered && !ChannelLocked && !_busy;

    public bool CanInstall { get; private set; }

    /// <summary>The service's refusal of the last thing pressed, until the next load.</summary>
    public string? Problem { get; private set; }

    /// <summary>The service answered the last load.</summary>
    public bool Answered { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var snapshot = await _gateway.LoadAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            Answered = false;
            CanInstall = false;
            Sentence = "The capture service did not answer, so whether an update is available is unknown.";
            return;
        }

        Answered = true;
        Sentence = snapshot.Sentence;
        InstalledVersion = snapshot.InstalledVersion;
        Channel = snapshot.Channel;
        ChannelLocked = snapshot.ChannelLocked;
        CanInstall = snapshot.CanInstall;
    }

    /// <summary>True when the service took the channel. The card is read again either way.</summary>
    public async Task<bool> ChooseAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        if (!CanChooseChannel || channel == Channel)
        {
            return false;
        }

        _busy = true;
        try
        {
            Problem = await _gateway.SetChannelAsync(channel, ct).ConfigureAwait(false);
            await LoadAsync(ct).ConfigureAwait(false);
            return Problem is null;
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        if (!CanInstall || _busy)
        {
            return false;
        }

        _busy = true;
        try
        {
            var refusal = await _gateway.InstallAsync(ct).ConfigureAwait(false);
            await LoadAsync(ct).ConfigureAwait(false);
            Problem = refusal;
            return refusal is null;
        }
        finally
        {
            _busy = false;
        }
    }
}

/// <summary>The Updates card's gateway, over the pipe (ST-113).</summary>
public sealed class PipeUpdates(CaptureConnection connection) : IUpdateGateway
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>The service's own report, for the tray notice as well as the card.</summary>
    public Task<UpdateReported?> ReportAsync(CancellationToken ct = default) =>
        _connection.RequestAsync<UpdateReported>(id => new GetUpdateCommand { RequestId = id }, ct);

    public async Task<UpdateSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        var reported = await ReportAsync(ct).ConfigureAwait(false);
        return reported is null ? null : Snapshot(reported);
    }

    public async Task<string?> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        var result = await _connection.SendAsync(id => new SetUpdateChannelCommand { RequestId = id, Channel = channel.ToWire() }, ct).ConfigureAwait(false);
        return result.Ok ? null : result.Error ?? "The capture service did not change the channel.";
    }

    public async Task<string?> InstallAsync(CancellationToken ct = default)
    {
        var result = await _connection.SendAsync(id => new InstallUpdateCommand { RequestId = id }, ct).ConfigureAwait(false);
        return result.Ok ? null : result.Error ?? "The capture service did not start the update.";
    }

    public static UpdateSnapshot Snapshot(UpdateReported reported)
    {
        ArgumentNullException.ThrowIfNull(reported);
        return new UpdateSnapshot(
            reported.State,
            reported.InstalledVersion,
            reported.AvailableVersion,
            UpdateChannels.TryParse(reported.Channel, out var channel) ? channel : UpdateChannel.Stable,
            reported.ChannelLocked,
            reported.Sentence,
            reported.CanInstall);
    }
}
