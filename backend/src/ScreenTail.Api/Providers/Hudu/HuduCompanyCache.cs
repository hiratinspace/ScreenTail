using System.Collections.Concurrent;

namespace ScreenTail.Api.Providers.Hudu;

/// <summary>
/// A tenant's Hudu companies, for ten minutes (ST-095 AC1). One instance for the deployment, keyed by
/// tenant, because the provider itself is built per request from the vault and would otherwise ask
/// Hudu the same question on every publish.
/// </summary>
public sealed class HuduCompanyCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, IReadOnlyList<CompanyRef> Companies)> _entries = new();

    public async Task<IReadOnlyList<CompanyRef>?> GetOrAddAsync(string key, TimeSpan keepFor, Func<CancellationToken, Task<IReadOnlyList<CompanyRef>?>> load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(load);
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && entry.Until > now)
        {
            return entry.Companies;
        }

        var companies = await load(ct).ConfigureAwait(false);
        if (companies is not null)
        {
            _entries[key] = (now + keepFor, companies);
        }

        return companies;
    }

    public void Forget(string key) => _entries.TryRemove(key, out _);
}
