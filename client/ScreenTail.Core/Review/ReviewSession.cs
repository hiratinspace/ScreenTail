using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// What Review does to a session: read it, write the edited note back, or throw it away (ST-074).
///
/// Three lines of plumbing, but with somewhere to put the reasons and somewhere to test them. Without it
/// the view model would take three delegates that nothing implements, which is how a feature ends up
/// green with no caller — and the discard in particular is irreversible, so it should not be reachable by
/// whatever the window happened to pass in.
/// </summary>
/// <summary>
/// The frame operations the centre pane needs (ST-075), named apart from the store so the render harness
/// can supply a fixture-backed one and the pane's rules stay testable without a database.
/// </summary>
public interface IReviewFrames
{
    /// <summary>The redacted image, or null when the frame is gone. Never a pending one (INV-1).</summary>
    Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default);

    Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default);

    /// <summary>Called when the 5 s undo window closes, never before it.</summary>
    Task<bool> DeleteAsync(string frameId, CancellationToken ct = default);

    Task BlurAsync(string frameId, ReadOnlyMemory<byte> image, MaskedRegion region, CancellationToken ct = default);
}

public sealed class ReviewSession(ISessionStore store, string sessionId) : IReviewFrames
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _sessionId = string.IsNullOrWhiteSpace(sessionId)
        ? throw new ArgumentException("A session id is required.", nameof(sessionId))
        : sessionId;

    /// <summary>Redacted frames only — that is <see cref="ISessionStore.LoadSessionAsync"/>'s guarantee (INV-1).</summary>
    public Task<Session?> LoadAsync(CancellationToken ct = default) => _store.LoadSessionAsync(_sessionId, ct);

    public Task SaveAsync(DraftNote note, CancellationToken ct = default) => _store.SaveDraftAsync(_sessionId, note, ct);

    public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _store.GetRedactedFrameImageAsync(frame.Id, ct);
    }

    /// <summary>
    /// `Space`. Persisted immediately, because the decision is usually "this must not leave the building"
    /// and the next thing that happens might be a crash.
    /// </summary>
    public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) =>
        _store.SetFrameExcludedAsync(frameId, !included, ct);

    public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) =>
        _store.DeleteFrameAsync(frameId, ct);

    public Task BlurAsync(string frameId, ReadOnlyMemory<byte> image, MaskedRegion region, CancellationToken ct = default) =>
        _store.ApplyUserBlurAsync(frameId, image, region, ct);

    /// <summary>
    /// Spec §5 S3 Discard, after the typed confirmation. The raw data goes now rather than at the next
    /// retention pass: a technician who discarded a session because of what was on the screen has said
    /// they want it gone, and "gone in a day" is not what they were told.
    /// </summary>
    public Task<int> DiscardAsync(CancellationToken ct = default) => _store.DiscardSessionAsync(_sessionId, ct);
}
