using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
/// lookup. The API key is treated the same way, as a <see cref="Fingerprint"/> held beside the site: a
/// list loaded under one key is neither joined nor served under another, so a rotated key is never
/// shown what only the old key could see. The key itself is never held here.
///
/// A tenant has at most one load in flight (#229 review). Without that, every publish arriving as the
/// entry expired or was forgotten paged the whole list itself: up to 400 requests each at 10,000
/// companies, all at once, against the customer's Hudu. Callers for the same tenant, site and key wait
/// on the one load and are all given its answer, a refusal included; another tenant's load is its own.
/// Only a successful list is kept, so a failed, thrown or stopped load leaves nothing behind and the
/// next caller loads again. A caller that gives up stops only its own wait: the load carries on for
/// the others, and is stopped only once nobody is left waiting for it.
///
/// <see cref="Forget"/> moves the tenant to a new generation, and so does a call for another site or
/// key while a load is out. A load that began before either still answers the callers who asked, but
/// its list is not kept, and a call after it starts its own load rather than joining the old one. The
/// old load is set aside, not cancelled: it keeps paging for its own waiters until it ends. That is
/// bounded, by the page and company caps and each request's timeout, and by being set aside at most
/// once per forget or switch, so it cannot multiply the way the loads before this change did.
/// </summary>
public sealed class HuduCompanyCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new();

    /// <summary>The key a tenant's entry is held under, for the provider and for whoever forgets it.</summary>
    public static string KeyFor(Guid tenantId) => tenantId.ToString();

    /// <summary>
    /// What a list is held against instead of the API key: the first eight bytes of the key's SHA-256,
    /// in hex. Enough to tell one key from another; never logged, and held only in memory.
    /// </summary>
    public static string Fingerprint(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)).AsSpan(0, 8));
    }

    /// <summary>
    /// The tenant's list from memory, or from <paramref name="load"/>, which is called at most once at a
    /// time per tenant, site and key. <paramref name="ct"/> stops this caller's wait and nothing else;
    /// the token <paramref name="load"/> is given is cancelled only when every caller waiting on it has
    /// gone. <paramref name="keyFingerprint"/> is <see cref="Fingerprint"/> of the key the load uses.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<CompanyRef>>> GetOrAddAsync(string key, string site, string keyFingerprint, TimeSpan keepFor, Func<CancellationToken, Task<ProviderResult<IReadOnlyList<CompanyRef>>>> load, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(keyFingerprint);
        ArgumentNullException.ThrowIfNull(load);
        ct.ThrowIfCancellationRequested();

        var source = new Source(site, keyFingerprint);
        var tenant = _tenants.GetOrAdd(key, _ => new Tenant());
        Flight flight;
        var starts = false;
        lock (tenant.Gate)
        {
            var now = time.GetUtcNow();
            if (tenant.Kept is { } kept && kept.Until > now && kept.Source == source)
            {
                return ProviderResult.Success(kept.Companies);
            }

            if (tenant.Loading is { } loading && loading.Source == source)
            {
                flight = loading;
            }
            else
            {
                if (tenant.Loading is not null)
                {
                    // A load for another site or key is still out. Its list is not this one's, so it is
                    // not kept over the one this load brings back.
                    tenant.Generation++;
                }

                flight = new Flight(source, tenant.Generation, now + keepFor);
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
    /// says a company on the list no longer exists. A tenant that never looked anything up has nothing
    /// to forget, and is not added for it.
    /// </summary>
    public void Forget(string key)
    {
        if (!_tenants.TryGetValue(key, out var tenant))
        {
            return;
        }

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
                    tenant.Kept = new Entry(flight.Until, flight.Source, result.Value!);
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

                // The last caller can give up between the load landing and this: the error is still
                // each remaining caller's to read, but is not reported as unobserved if none is left.
                _ = flight.Answer.Task.Exception;
            }
        }
    }

    /// <summary>The load is over: no caller from here on joins it. Called under the tenant's gate.</summary>
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

            // Its callbacks run elsewhere, so nothing the load does on cancellation runs under this gate.
            // One that throws faults the returned task, which is read here rather than left unobserved.
            _ = flight.Stop.CancelAsync().ContinueWith(
                static stopped => _ = stopped.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Where a list came from: the site, and the fingerprint of the key it was read with.</summary>
    private readonly record struct Source(string Site, string KeyFingerprint);

    private sealed record Entry(DateTimeOffset Until, Source Source, IReadOnlyList<CompanyRef> Companies);

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
    private sealed class Flight(Source source, long generation, DateTimeOffset until)
    {
        public Source Source { get; } = source;

        public long Generation { get; } = generation;

        /// <summary>When the list expires, counted from when it was first asked for, as before.</summary>
        public DateTimeOffset Until { get; } = until;

        /// <summary>
        /// The answer every waiter reads. Its continuations run on their own, so one caller doing slow work
        /// straight after its await holds up neither the others nor the thread that answered.
        /// </summary>
        public TaskCompletionSource<ProviderResult<IReadOnlyList<CompanyRef>>> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Stop { get; } = new();

        public int Waiters { get; set; }

        public bool Landed { get; set; }

        public bool Abandoned { get; set; }
    }
}
