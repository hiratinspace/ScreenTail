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
/// Each check and take is one step under a lock, so requests arriving together cannot all pass a count
/// none of them has added to yet.
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

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _taken = [];
    private readonly Dictionary<Guid, DateTimeOffset> _refusedUntil = [];
    private readonly Dictionary<Guid, Dictionary<string, DateTimeOffset>> _newCompanies = [];

    /// <summary>Takes one of the device's requests for this window, or says how long until one is free.</summary>
    public TimeSpan? TryTake(Guid deviceId) => TryTake(deviceId, out _);

    /// <param name="firstRefusal">True for the first refusal of this device's window: the one the owner is told about.</param>
    /// <returns>Null when taken; else how long to wait.</returns>
    public TimeSpan? TryTake(Guid deviceId, out bool firstRefusal)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Forget(now);
            if (!_taken.TryGetValue(deviceId, out var mine))
            {
                mine = new Queue<DateTimeOffset>();
                _taken[deviceId] = mine;
            }

            if (mine.Count < PerDevice)
            {
                mine.Enqueue(now);
                firstRefusal = false;
                return null;
            }

            var wait = mine.Peek() + Window - now;
            if (wait < TimeSpan.FromSeconds(1))
            {
                wait = TimeSpan.FromSeconds(1);
            }

            firstRefusal = !_refusedUntil.ContainsKey(deviceId);
            _refusedUntil[deviceId] = now + wait;
            return wait;
        }
    }

    /// <summary>
    /// Takes one of the device's new PSA companies for the day: false when it has named
    /// <see cref="NewCompaniesPerDevice"/> others already. Naming the same company again takes nothing.
    /// </summary>
    public bool TryTakeNewCompany(Guid deviceId, string psaCompany)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Forget(now);
            if (!_newCompanies.TryGetValue(deviceId, out var named))
            {
                named = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                _newCompanies[deviceId] = named;
            }

            if (named.ContainsKey(psaCompany))
            {
                return true;
            }

            if (named.Count >= NewCompaniesPerDevice)
            {
                return false;
            }

            named[psaCompany] = now;
            return true;
        }
    }

    /// <summary>Drops what has aged out, for every device, so idle devices are not kept.</summary>
    private void Forget(DateTimeOffset now)
    {
        foreach (var (device, stamps) in _taken.ToList())
        {
            while (stamps.Count > 0 && stamps.Peek() <= now - Window)
            {
                _ = stamps.Dequeue();
            }

            if (stamps.Count == 0)
            {
                _ = _taken.Remove(device);
            }
        }

        foreach (var (device, until) in _refusedUntil.ToList())
        {
            if (until <= now)
            {
                _ = _refusedUntil.Remove(device);
            }
        }

        foreach (var (device, named) in _newCompanies.ToList())
        {
            foreach (var (company, at) in named.ToList())
            {
                if (at <= now - NewCompanyWindow)
                {
                    _ = named.Remove(company);
                }
            }

            if (named.Count == 0)
            {
                _ = _newCompanies.Remove(device);
            }
        }
    }
}
