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
/// lookup.
///
/// A tenant has at most one load in flight (#229 review). Without that, every publish arriving as the
/// entry expired or was forgotten paged the whole list itself: up to 400 requests each at 10,000
/// companies, all at once, against the customer's Hudu. Callers for the same tenant and site wait on
/// the one load and are all given its answer, a refusal included; another tenant's load is its own.
/// Only a successful list is kept, so a failed, thrown or stopped load leaves nothing behind and the
/// next caller loads again. A caller that gives up stops only its own wait: the load carries on for
/// the others, and is stopped only once nobody is left waiting for it.
///
/// <see cref="Forget"/> moves the tenant to a new generation. A load that began before it still
/// answers the callers who asked, but its list is not kept, and a call after the forget starts its own
/// load rather than joining the old one. A provider built under the old key whose load only starts
/// after the forget is not seen that way: it can still store its list, until the entry expires. That
/// list is from the right site, and Hudu itself refuses an id the new key may not see, so the cost is a
/// refusal rather than the wrong customer.
/// </summary>
public sealed class HuduCompanyCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new();

    /// <summary>The key a tenant's entry is held under, for the provider and for whoever forgets it.</summary>
    public static string KeyFor(Guid tenantId) => tenantId.ToString();

    /// <summary>
    /// The tenant's list from memory, or from <paramref name="load"/>, which is called at most once at a
    /// time per tenant and site. <paramref name="ct"/> stops this caller's wait and nothing else; the
    /// token <paramref name="load"/> is given is cancelled only when every caller waiting on it has gone.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<CompanyRef>>> GetOrAddAsync(string key, string site, TimeSpan keepFor, Func<CancellationToken, Task<ProviderResult<IReadOnlyList<CompanyRef>>>> load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(site);
        ct.ThrowIfCancellationRequested();

        var tenant = _tenants.GetOrAdd(key, _ => new Tenant());
        Flight flight;
        var starts = false;
        lock (tenant.Gate)
        {
            var now = time.GetUtcNow();
            if (tenant.Kept is { } kept && kept.Until > now && string.Equals(kept.Site, site, StringComparison.Ordinal))
            {
                return ProviderResult.Success(kept.Companies);
            }

            if (tenant.Loading is { } loading && string.Equals(loading.Site, site, StringComparison.Ordinal))
            {
                flight = loading;
            }
            else
            {
                if (tenant.Loading is not null)
                {
                    // A load for another site is still out. Its list is not this site's, so it is not kept
                    // over the one this load brings back.
                    tenant.Generation++;
                }

                flight = new Flight(site, tenant.Generation, now + keepFor);
                tenant.Loading = flight;
                starts = true;
            }

            flight.Waiters++;
        }

        if (starts)
        {
            _ = RunAsync(tenant, flight, load);
        }

        try
        {
            return await flight.Answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Leave(tenant, flight);
        }
    }

    /// <summary>
    /// Drops the tenant's entry, so the next lookup asks the site, and keeps a load already out from
    /// storing what it brings back. Called when the tenant's Hudu is replaced or removed, and when Hudu
    /// says a company on the list no longer exists.
    /// </summary>
    public void Forget(string key)
    {
        var tenant = _tenants.GetOrAdd(key, _ => new Tenant());
        lock (tenant.Gate)
        {
            tenant.Kept = null;
            tenant.Loading = null;
            tenant.Generation++;
        }
    }

    private static async Task RunAsync(Tenant tenant, Flight flight, Func<CancellationToken, Task<ProviderResult<IReadOnlyList<CompanyRef>>>> load)
    {
        try
        {
            var result = await load(flight.Stop.Token).ConfigureAwait(false);
            lock (tenant.Gate)
            {
                Land(tenant, flight);
                if (result.Ok && !flight.Abandoned && flight.Generation == tenant.Generation)
                {
                    tenant.Kept = new Entry(flight.Until, flight.Site, result.Value!);
                }
            }

            flight.Answer.TrySetResult(result);
        }
        catch (Exception ex)
        {
            bool abandoned;
            lock (tenant.Gate)
            {
                Land(tenant, flight);
                abandoned = flight.Abandoned;
            }

            // Nobody is waiting on a load that was stopped for want of callers, so its end is a
            // cancellation rather than an error left for no one to read.
            if (abandoned)
            {
                flight.Answer.TrySetCanceled();
            }
            else
            {
                flight.Answer.TrySetException(ex);
            }
        }
    }

    /// <summary>The load is over: no caller from here on joins it. Called under the tenant's lock.</summary>
    private static void Land(Tenant tenant, Flight flight)
    {
        flight.Landed = true;
        if (ReferenceEquals(tenant.Loading, flight))
        {
            tenant.Loading = null;
        }
    }

    /// <summary>One caller has its answer or has given up. The last to give up on a load still out stops it.</summary>
    private static void Leave(Tenant tenant, Flight flight)
    {
        lock (tenant.Gate)
        {
            flight.Waiters--;
            if (flight.Waiters > 0 || flight.Landed || flight.Abandoned)
            {
                return;
            }

            flight.Abandoned = true;
            if (ReferenceEquals(tenant.Loading, flight))
            {
                tenant.Loading = null;
            }

            // Its callbacks run elsewhere, so nothing the load does on cancellation runs under this lock.
            _ = flight.Stop.CancelAsync();
        }
    }

    private sealed record Entry(DateTimeOffset Until, string Site, IReadOnlyList<CompanyRef> Companies);

    /// <summary>One tenant's kept list and load in flight. Every other field is read and written under its gate.</summary>
    private sealed class Tenant
    {
        public Lock Gate { get; } = new();

        public Entry? Kept { get; set; }

        public Flight? Loading { get; set; }

        public long Generation { get; set; }
    }

    /// <summary>
    /// One list load and the callers waiting on it. The token source is never disposed: it has no timer
    /// and nobody asks for its wait handle, so there is nothing to release, and disposing it could race
    /// the last caller stopping it.
    /// </summary>
    private sealed class Flight(string site, long generation, DateTimeOffset until)
    {
        public string Site { get; } = site;

        public long Generation { get; } = generation;

        /// <summary>When the list expires, counted from when it was first asked for, as before.</summary>
        public DateTimeOffset Until { get; } = until;

        public TaskCompletionSource<ProviderResult<IReadOnlyList<CompanyRef>>> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Stop { get; } = new();

        public int Waiters { get; set; }

        public bool Landed { get; set; }

        public bool Abandoned { get; set; }
    }
}
