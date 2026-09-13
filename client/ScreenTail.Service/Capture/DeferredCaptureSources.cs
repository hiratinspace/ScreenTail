using ScreenTail.Core.Sessions;

namespace ScreenTail.Service.Capture;

/// <summary>
/// Stands in for the capture sources until the things they need exist.
///
/// The state machine is built early — it has to recover the sessions a crash left behind before the pipe
/// starts serving, so the first client to connect sees the outcome rather than the orphan — while the
/// sources need the screenshot capturer and the scope coordinator, which come later. Before this, the host
/// passed <see cref="NoCaptureSources"/> and never replaced it, so "mark moment" reached a method that did
/// nothing: the marker was written and the frame it was meant to point at never existed.
/// </summary>
internal sealed class DeferredCaptureSources : ICaptureSources
{
    private ICaptureSources _inner = new NoCaptureSources();

    public void Use(ICaptureSources sources) => _inner = sources;

    public Task StartAsync(SessionMachine machine, CancellationToken ct = default) => _inner.StartAsync(machine, ct);

    public Task StopAsync(CancellationToken ct = default) => _inner.StopAsync(ct);

    public Task MarkMomentAsync(CancellationToken ct = default) => _inner.MarkMomentAsync(ct);
}
