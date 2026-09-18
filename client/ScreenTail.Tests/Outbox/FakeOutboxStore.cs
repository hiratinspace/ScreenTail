using ScreenTail.Core.Outbox;

namespace ScreenTail.Tests.Outbox;

/// <summary>
/// The queue in a list, for the rules tests.
///
/// <see cref="OutboxStoreTests"/> runs the same rules against the real encrypted store, which is where
/// durability and retention are actually decided. This one exists so the state machine can be tested
/// without a database file per test.
/// </summary>
internal sealed class FakeOutboxStore : IOutboxStore
{
    public List<OutboxItem> Items { get; } = [];

    public Task<bool> EnqueueAsync(OutboxItem item, CancellationToken ct = default)
    {
        if (Items.Exists(existing => existing.IdempotencyKey == item.IdempotencyKey
            && existing.State is OutboxState.Pending or OutboxState.Uncertain or OutboxState.Done))
        {
            return Task.FromResult(false);
        }

        Items.Add(item);
        return Task.FromResult(true);
    }

    public Task<OutboxItem?> TakeDueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(item => item.State == OutboxState.Pending && item.DueAt <= now)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault());

    public Task<OutboxItem?> TakeUncertainAsync(CancellationToken ct = default) =>
        Task.FromResult(Items
            .Where(item => item.State == OutboxState.Uncertain)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault());

    public Task UpdateAsync(OutboxItem item, CancellationToken ct = default)
    {
        var at = Items.FindIndex(existing => existing.Id == item.Id);
        if (at >= 0)
        {
            Items[at] = item;
        }

        return Task.CompletedTask;
    }

    public Task<OutboxWaiting> CountWaitingAsync(CancellationToken ct = default) => Task.FromResult(new OutboxWaiting(
        Items.Count(i => i.State == OutboxState.Pending && i.Kind == OutboxKind.Draft),
        Items.Count(i => i.State == OutboxState.Pending && i.Kind != OutboxKind.Draft),
        Items.Count(i => i.State == OutboxState.Uncertain)));
}
