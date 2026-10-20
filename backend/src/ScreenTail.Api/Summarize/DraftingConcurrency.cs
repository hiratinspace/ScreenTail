using ScreenTail.Api.Auth;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// How many drafts one device and one tenant may have in flight at once (ST-063; 2026-10-12 review).
///
/// A drafting request binds a bundle of up to 16 MB before anything else looks at it, and holds it,
/// with a UTF-8 copy for the model, for as long as the model takes: up to a minute. The only bound on
/// how many of those were in memory together was the per-tenant cost reservation, about a hundred
/// sessions' worth, and nothing across tenants. A looping client, or a compromised one, could hold
/// gigabytes. So the endpoint is gated <b>before the body is read</b>, by
/// <see cref="Use"/>, and a request over the limit is answered 429 busy, which the
/// client's outbox queues and asks again.
///
/// Two per device, because a device finishes a session at a time and two covers a retry overlapping a
/// new one. Ten per tenant, because a tenant is a team, and ten is more technicians finishing in the
/// same minute than an MSP has.
///
/// <b>A count, not a timer.</b> The framework's rate limiter keeps partitions on a heartbeat and sweeps
/// idle ones; this keeps an entry only while something is in flight and removes it when the last one
/// leaves, so what it holds is bounded by the requests running now rather than by every device that has
/// ever asked, and nothing runs while nobody is drafting.
/// </summary>
public sealed class DraftingConcurrency
{
    /// <summary>Drafts one device may have in flight.</summary>
    public const int PerDevice = 2;

    /// <summary>Drafts one tenant may have in flight, across all its devices.</summary>
    public const int PerTenant = 10;

    /// <summary>Marks an endpoint the gate applies to. <see cref="SummarizeEndpoint"/> carries it.</summary>
    public static readonly object Gated = new GatedEndpoint();

    /// <summary>Where the request's lease waits, so the endpoint can tell it went through the gate.</summary>
    private static readonly object LeaseKey = new();

    private readonly Dictionary<Guid, int> _devices = [];
    private readonly Dictionary<Guid, int> _tenants = [];
    private readonly Lock _lock = new();

    /// <summary>How many devices and tenants have something in flight. Zero when nobody is drafting.</summary>
    public int Tracked
    {
        get
        {
            lock (_lock)
            {
                return _devices.Count + _tenants.Count;
            }
        }
    }

    /// <summary>
    /// A place for one draft, or null with the reason when the device or its tenant has none left.
    ///
    /// Both limits are checked before either is counted, under one lock, so a request the tenant turns
    /// away never holds one of its device's places.
    /// </summary>
    public IDisposable? TryEnter(Guid tenantId, Guid deviceId, out string? refusal)
    {
        lock (_lock)
        {
            if (_devices.GetValueOrDefault(deviceId) >= PerDevice)
            {
                refusal = "This device is already drafting as many sessions as it may at once. The session is queued and will be drafted shortly.";
                return null;
            }

            if (_tenants.GetValueOrDefault(tenantId) >= PerTenant)
            {
                refusal = "This tenant is already drafting as many sessions as it may at once. The session is queued and will be drafted shortly.";
                return null;
            }

            _devices[deviceId] = _devices.GetValueOrDefault(deviceId) + 1;
            _tenants[tenantId] = _tenants.GetValueOrDefault(tenantId) + 1;
        }

        refusal = null;
        return new Lease(this, tenantId, deviceId);
    }

    /// <summary>
    /// Gates every endpoint carrying <see cref="Gated"/>. After authorization, so the caller is known,
    /// and before the endpoint, so the body has not been read.
    /// </summary>
    public static IApplicationBuilder Use(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // One gate per application: a test host gets its own, and nothing in the container has to know.
        var gate = new DraftingConcurrency();
        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<GatedEndpoint>() is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // Already proved by CallerPolicy, so this is a read of what it stashed. A route that reached
            // here without a caller is refused the way the policy would refuse it.
            if (await Caller.BindAsync(context).ConfigureAwait(false) is not { } who)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var lease = gate.TryEnter(who.TenantId, who.DeviceId, out var refusal);
            if (lease is null)
            {
                await Results.Json(new SummarizeResponse("busy", refusal), statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            context.Items[LeaseKey] = lease;
            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Whether this request holds a place. The endpoint refuses to draft without one, so a deployment
    /// that lost the middleware line fails closed rather than drafting without a limit.
    /// </summary>
    public static bool Holds(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.ContainsKey(LeaseKey);
    }

    private void Exit(Guid tenantId, Guid deviceId)
    {
        lock (_lock)
        {
            Decrement(_devices, deviceId);
            Decrement(_tenants, tenantId);
        }
    }

    private static void Decrement(Dictionary<Guid, int> counts, Guid key)
    {
        var left = counts.GetValueOrDefault(key) - 1;
        if (left <= 0)
        {
            _ = counts.Remove(key);
        }
        else
        {
            counts[key] = left;
        }
    }

    private sealed class GatedEndpoint;

    /// <summary>One place, given back once however often it is disposed.</summary>
    private sealed class Lease(DraftingConcurrency gate, Guid tenantId, Guid deviceId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Exit(tenantId, deviceId);
            }
        }
    }
}
