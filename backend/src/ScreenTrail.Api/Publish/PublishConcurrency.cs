using ScreenTrail.Api.Auth;

namespace ScreenTrail.Api.Publish;

/// <summary>
/// How many publishes one device and one tenant may have in flight at once (2026-11-15 review).
///
/// A publish binds a body of up to 32 MB before anything else looks at it, decodes its screenshots, and
/// holds all of it until every destination has answered: up to the publish deadline, 75 s. A PSA site is
/// the tenant's choice, so a slow one (or one that stalls each upload on purpose) holds that memory for
/// the whole deadline, and nothing bounded how many such requests one device could open. So the endpoint
/// is gated <b>before the body is read</b>, the way drafting is
/// (<see cref="Summarize.DraftingConcurrency"/>), and a request over the limit is refused with 429 busy.
///
/// The client reads a 4xx as the backend's refusal before it acted: the publish record goes back to how
/// it was, nothing is marked unknown, and the technician is shown <c>message</c>, which says to publish
/// again in a moment. Nothing is lost: the note is still in Review.
///
/// Two per device, because a technician publishes one session at a time, and two covers a Retry
/// overlapping the publish before it. Ten per tenant, as for drafting: more technicians pressing Publish
/// in the same minute than an MSP has.
///
/// <b>A count, not a timer</b>, like the drafting gate: an entry exists only while something is in
/// flight, so what it holds is bounded by the requests running now.
/// </summary>
public sealed class PublishConcurrency
{
    /// <summary>Publishes one device may have in flight.</summary>
    public const int PerDevice = 2;

    /// <summary>Publishes one tenant may have in flight, across all its devices.</summary>
    public const int PerTenant = 10;

    /// <summary>The seconds named in Retry-After on a refusal. A publish usually answers within a few.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>Marks an endpoint the gate applies to. <see cref="Endpoints.PublishEndpoint"/> carries it.</summary>
    public static readonly object Gated = new GatedEndpoint();

    private static readonly object LeaseKey = new();

    private readonly Dictionary<Guid, int> _devices = [];
    private readonly Dictionary<Guid, int> _tenants = [];
    private readonly Lock _lock = new();

    /// <summary>How many devices and tenants have something in flight. Zero when nobody is publishing.</summary>
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
    /// A place for one publish, or null with the reason when the device or its tenant has none left. Both
    /// limits are checked before either is counted, under one lock, so a request the tenant turns away
    /// never holds one of its device's places.
    /// </summary>
    public IDisposable? TryEnter(Guid tenantId, Guid deviceId, out string? refusal)
    {
        lock (_lock)
        {
            if (_devices.GetValueOrDefault(deviceId) >= PerDevice)
            {
                refusal = "This device is already publishing as much as it may at once. Nothing was sent; publish again in a moment.";
                return null;
            }

            if (_tenants.GetValueOrDefault(tenantId) >= PerTenant)
            {
                refusal = "Your team is already publishing as much as it may at once. Nothing was sent; publish again in a moment.";
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

        // One gate per application: a test host gets its own. Nothing registers one in production; a
        // test that wants to watch the count registers its own.
        var gate = app.ApplicationServices.GetService<PublishConcurrency>() ?? new PublishConcurrency();
        return app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<GatedEndpoint>() is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // Already proved by CallerPolicy, so this reads what it stashed.
            if (await Caller.BindAsync(context).ConfigureAwait(false) is not { } who)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var lease = gate.TryEnter(who.TenantId, who.DeviceId, out var refusal);
            if (lease is null)
            {
                // The body is read into nothing first, holding no place and no memory, so a slow upload
                // gets this answer rather than a dropped connection (UnreadBody).
                if (!await Endpoints.UnreadBody.DiscardAsync(context).ConfigureAwait(false))
                {
                    return;
                }

                // The shape of every other refusal on this route ({error, message}), which is what the
                // client reads its sentence from.
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Json(new { error = "busy", message = refusal }, statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            context.Items[LeaseKey] = lease;
            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Whether this request holds a place. The endpoint refuses to publish without one, so a deployment
    /// that lost the middleware line fails closed rather than publishing without a limit.
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
    private sealed class Lease(PublishConcurrency gate, Guid tenantId, Guid deviceId) : IDisposable
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
