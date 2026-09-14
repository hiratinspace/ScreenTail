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
public sealed class ReviewSession(ISessionStore store, string sessionId)
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _sessionId = string.IsNullOrWhiteSpace(sessionId)
        ? throw new ArgumentException("A session id is required.", nameof(sessionId))
        : sessionId;

    /// <summary>Redacted frames only — that is <see cref="ISessionStore.LoadSessionAsync"/>'s guarantee (INV-1).</summary>
    public Task<Session?> LoadAsync(CancellationToken ct = default) => _store.LoadSessionAsync(_sessionId, ct);

    public Task SaveAsync(DraftNote note, CancellationToken ct = default) => _store.SaveDraftAsync(_sessionId, note, ct);

    /// <summary>
    /// Spec §5 S3 Discard, after the typed confirmation. The raw data goes now rather than at the next
    /// retention pass: a technician who discarded a session because of what was on the screen has said
    /// they want it gone, and "gone in a day" is not what they were told.
    /// </summary>
    public Task<int> DiscardAsync(CancellationToken ct = default) => _store.DiscardSessionAsync(_sessionId, ct);
}
