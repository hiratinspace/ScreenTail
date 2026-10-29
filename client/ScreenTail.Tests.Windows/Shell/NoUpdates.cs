using ScreenTail.Core.Settings;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>An Updates card whose service has nothing to say, for shell tests about the other panels.</summary>
internal sealed class NoUpdates : IUpdateGateway
{
    public Task<UpdateSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult<UpdateSnapshot?>(null);

    public Task<string?> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default) => Task.FromResult<string?>("not under test");

    public Task<string?> InstallAsync(CancellationToken ct = default) => Task.FromResult<string?>("not under test");
}
