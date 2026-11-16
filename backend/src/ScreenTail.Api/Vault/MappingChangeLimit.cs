namespace ScreenTail.Api.Vault;

/// <summary>
/// How many map and unmap requests one device may make (2026-11-15 review): <see cref="PerDevice"/> in any
/// <see cref="Window"/>, so a broken or hostile device cannot bury earlier changes under new ones or grow
/// the record without end. The check and the take are one step under a lock, so requests arriving
/// together cannot all pass a count none of them has added to yet.
///
/// In process, by design: the deployment runs one API instance (ST-007, <c>deploy/</c>). A second
/// instance would give a device this many per instance; if that changes, the limit moves to the database.
/// </summary>
public sealed class MappingChangeLimit(TimeProvider time)
{
    public const int PerDevice = 30;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _taken = [];

    /// <summary>Takes one of the device's requests for this window, or says how long until one is free.</summary>
    /// <returns>Null when taken; else how long to wait.</returns>
    public TimeSpan? TryTake(Guid deviceId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            // Forget windows that have passed, for every device, so the map does not keep idle ones.
            foreach (var (device, stamps) in _taken.ToList())
            {
                while (stamps.Count > 0 && stamps.Peek() <= now - Window)
                {
                    _ = stamps.Dequeue();
                }

                if (stamps.Count == 0 && device != deviceId)
                {
                    _ = _taken.Remove(device);
                }
            }

            if (!_taken.TryGetValue(deviceId, out var mine))
            {
                mine = new Queue<DateTimeOffset>();
                _taken[deviceId] = mine;
            }

            if (mine.Count >= PerDevice)
            {
                var wait = mine.Peek() + Window - now;
                return wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1);
            }

            mine.Enqueue(now);
            return null;
        }
    }
}
