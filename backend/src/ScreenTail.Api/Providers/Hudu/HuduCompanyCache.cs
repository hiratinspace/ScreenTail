using System.Collections.Concurrent;

namespace ScreenTail.Api.Providers.Hudu;

/// <summary>
/// A tenant's Hudu companies, for ten minutes (ST-095 AC1). One instance for the deployment, keyed by
/// tenant, because the provider itself is built per request from the vault and would otherwise ask
/// Hudu the same question on every publish.
///
/// Each entry remembers which site it came from, and a lookup for another site is a miss (2026-10-12
/// review, S-71 and T-214). Company ids are numbered per site, so a list from the old site would check
/// an id against the wrong customers for up to ten minutes after the tenant moved. The site is kept
/// beside the list rather than in the key, so one tenant holds one entry however often it moves, and a
/// backend that did not serve a move to another site still stops trusting the old list at its next
/// lookup. A new key for the same site is not seen that way: another backend keeps the old key's list
/// until the entry expires, and a fetch that began under the old key just before a forget can store
/// it here again. That list is from the right site, and Hudu itself refuses an id the new key may not
/// see, so the cost is a refusal rather than the wrong customer.
/// </summary>
public sealed class HuduCompanyCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Site, IReadOnlyList<CompanyRef> Companies)> _entries = new();

    /// <summary>The key a tenant's entry is held under, for the provider and for whoever forgets it.</summary>
    public static string KeyFor(Guid tenantId) => tenantId.ToString();

    public async Task<IReadOnlyList<CompanyRef>?> GetOrAddAsync(string key, string site, TimeSpan keepFor, Func<CancellationToken, Task<IReadOnlyList<CompanyRef>?>> load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(site);
        var now = time.GetUtcNow();
        if (_entries.TryGetValue(key, out var entry) && entry.Until > now && string.Equals(entry.Site, site, StringComparison.Ordinal))
        {
            return entry.Companies;
        }

        var companies = await load(ct).ConfigureAwait(false);
        if (companies is not null)
        {
            _entries[key] = (now + keepFor, site, companies);
        }

        return companies;
    }

    /// <summary>Drops the tenant's entry, so the next lookup asks the site. Called when the tenant's Hudu is replaced or removed.</summary>
    public void Forget(string key) => _entries.TryRemove(key, out _);
}
