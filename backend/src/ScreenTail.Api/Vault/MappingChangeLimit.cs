using System.Collections.Concurrent;

namespace ScreenTail.Api.Vault;

/// <summary>
/// How much one device may change company mappings (2026-11-15 review), so a broken or hostile device
/// cannot bury earlier changes under new ones or grow the record without end:
/// <list type="bullet">
/// <item><see cref="PerDevice"/> map or unmap requests in any <see cref="Window"/>;</item>
/// <item><see cref="NewCompaniesPerDevice"/> PSA companies with no current mapping in any <see cref="NewCompanyWindow"/>.
/// The PSA company is a name the device sends: the PSA has no company lookup to check it against
/// (the documentation company is checked), so this is what bounds how many names it can add.</item>
/// </list>
/// Each check and take is one step under the device's own lock, so requests arriving together cannot
/// all pass a count none of them has added to yet. A call expires only its own device's old entries, so
/// its cost does not depend on how many other devices there are; devices idle past the longest window
/// are dropped by a sweep that runs at most once every <see cref="SweepEvery"/>.
///
/// In process, by design: the deployment runs one API instance (ST-007, <c>deploy/</c>). A second
/// instance would give a device this much per instance, and a restart starts the windows again; if the
/// deployment changes, the limits move to the database.
/// </summary>
public sealed class MappingChangeLimit(TimeProvider time)
{
    public const int PerDevice = 30;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public const int NewCompaniesPerDevice = 500;

    public static readonly TimeSpan NewCompanyWindow = TimeSpan.FromDays(1);

    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Device> _devices = new();
    private long _lastSweepTicks = time.GetUtcNow().UtcTicks;

    /// <summary>How many devices are held now: for the tests that check idle ones are dropped.</summary>
    public int DevicesHeld => _devices.Count;

    /// <summary>Takes one of the device's requests for this window, or says how long until one is free.</summary>
    public TimeSpan? TryTake(Guid deviceId) => TryTake(deviceId, out _);

    /// <param name="firstRefusal">True for the first refusal of this device's window: the one the owner is told about.</param>
    /// <returns>Null when taken; else how long to wait.</returns>
    public TimeSpan? TryTake(Guid deviceId, out bool firstRefusal)
    {
        var now = time.GetUtcNow();
        var refused = With(deviceId, now, device =>
        {
            while (device.Taken.Count > 0 && device.Taken.Peek() <= now - Window)
            {
                _ = device.Taken.Dequeue();
            }

            if (device.Taken.Count < PerDevice)
            {
                device.Taken.Enqueue(now);
                return ((TimeSpan?)null, false);
            }

            var wait = device.Taken.Peek() + Window - now;
            if (wait < TimeSpan.FromSeconds(1))
            {
                wait = TimeSpan.FromSeconds(1);
            }

            var first = device.RefusedUntil is not { } until || until <= now;
            device.RefusedUntil = now + wait;
            return ((TimeSpan?)wait, first);
        });
        firstRefusal = refused.Item2;
        return refused.Item1;
    }

    /// <summary>
    /// Takes one of the device's new PSA companies for the day: false when it has named
    /// <see cref="NewCompaniesPerDevice"/> others already. Naming the same company again takes nothing.
    /// </summary>
    public bool TryTakeNewCompany(Guid deviceId, string psaCompany)
    {
        var now = time.GetUtcNow();
        return With(deviceId, now, device => TakeNewCompany(device, psaCompany, now));
    }

    private static bool TakeNewCompany(Device device, string psaCompany, DateTimeOffset now)
    {
        while (device.NewOrder.Count > 0 && device.NewOrder.Peek().At <= now - NewCompanyWindow)
        {
            _ = device.NewSet.Remove(device.NewOrder.Dequeue().Company);
        }

        if (device.NewSet.Contains(psaCompany))
        {
            return true;
        }

        if (device.NewSet.Count >= NewCompaniesPerDevice)
        {
            return false;
        }

        _ = device.NewSet.Add(psaCompany);
        device.NewOrder.Enqueue((psaCompany, now));
        return true;
    }

    /// <summary>Runs <paramref name="work"/> under the device's lock, on state the sweep has not dropped.</summary>
    private T With<T>(Guid deviceId, DateTimeOffset now, Func<Device, T> work)
    {
        SweepIfDue(now);
        while (true)
        {
            var device = _devices.GetOrAdd(deviceId, _ => new Device());
            lock (device.Gate)
            {
                if (device.Dropped)
                {
                    continue;
                }

                device.LastSeen = now;
                return work(device);
            }
        }
    }

    /// <summary>Drops devices idle past the longest window, at most once every <see cref="SweepEvery"/>.</summary>
    private void SweepIfDue(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepEvery.Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        var idleSince = now - (NewCompanyWindow > Window ? NewCompanyWindow : Window);
        foreach (var (id, device) in _devices)
        {
            lock (device.Gate)
            {
                if (device.LastSeen > idleSince)
                {
                    continue;
                }

                device.Dropped = true;
                _ = _devices.TryRemove(new KeyValuePair<Guid, Device>(id, device));
            }
        }
    }

    private sealed class Device
    {
        public Lock Gate { get; } = new();

        public Queue<DateTimeOffset> Taken { get; } = new();

        public DateTimeOffset? RefusedUntil { get; set; }

        public Queue<(string Company, DateTimeOffset At)> NewOrder { get; } = new();

        public HashSet<string> NewSet { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastSeen { get; set; }

        public bool Dropped { get; set; }
    }
}
