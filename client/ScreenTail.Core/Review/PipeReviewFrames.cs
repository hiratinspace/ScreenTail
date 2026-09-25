using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// The Review pane's view of a session when the store is on the other side of the pipe, which in the
/// running application it always is (ST-085 remainder).
///
/// Every method is a screen asking, so nothing here throws for an ordinary refusal: a frame that is gone
/// is a null image, a service that is not there is a failed result. <see cref="CaptureConnection"/> has
/// the same rule and this leans on it rather than repeating it.
/// </summary>
public sealed class PipeReviewFrames(CaptureConnection connection) : IReviewFrames
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>The session as the service holds it: redacted frames only, with its draft. Null when there is no such session or no service.</summary>
    public async Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default) =>
        (await _connection.RequestAsync<SessionLoaded>(id => new GetSessionCommand { RequestId = id, SessionId = sessionId }, ct).ConfigureAwait(false))?.Session;

    /// <summary>The whole note, written back. False when it did not land, which the editor shows as "Not saved — retrying".</summary>
    public async Task<bool> SaveDraftAsync(string sessionId, DraftNote draft, CancellationToken ct = default) =>
        (await _connection.SendAsync(id => new SaveDraftCommand { RequestId = id, SessionId = sessionId, Draft = draft }, ct).ConfigureAwait(false)).Ok;

    public async Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return (await _connection.RequestAsync<FrameLoaded>(id => new GetFrameCommand { RequestId = id, FrameId = frame.Id }, ct).ConfigureAwait(false))?.Image;
    }

    public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) =>
        _connection.SendAsync(id => new SetFrameIncludedCommand { RequestId = id, FrameId = frameId, Included = included }, ct);

    public async Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) =>
        (await _connection.SendAsync(id => new DeleteFrameCommand { RequestId = id, FrameId = frameId }, ct).ConfigureAwait(false)).Ok;

    public async Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        var reply = await _connection.RequestAsync<FrameLoaded>(
            id => new BlurFrameCommand { RequestId = id, FrameId = frameId, X = region.X, Y = region.Y, Width = region.Width, Height = region.Height },
            ct).ConfigureAwait(false);
        return reply?.Image;
    }
}
