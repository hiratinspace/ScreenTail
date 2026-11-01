using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ScreenTail.Api.Auth;

/// <summary>
/// How many anonymous calls one client may make (ST-099 hardening, ADR-0009). Every default is the
/// closed one: ten calls a minute from one address, which is several sign-ins for a person and very few
/// guesses for anybody else.
/// </summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    /// <summary>Calls one client address may make to the limited routes in one window.</summary>
    public int PermitLimit { get; set; } = 10;

    /// <summary>The window those calls are counted in. A refused call is told to wait this long.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Refuses, at startup, a limit that would not limit anything or would lock everyone out for good.</summary>
    public void Validate()
    {
        if (PermitLimit is < 1 or > 100_000)
        {
            throw new InvalidOperationException("RateLimit:PermitLimit must be between 1 and 100000.");
        }

        if (Window < TimeSpan.FromSeconds(1) || Window > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("RateLimit:Window must be between 1 second and 1 hour.");
        }
    }
}

/// <summary>
/// The limit on the routes anybody can call without a session or a token: the admin's sign-in nonce,
/// sign-in, signup and sign-out, and a device's activation with an invite code (ST-099 hardening,
/// ADR-0009). Each costs a signature check, a database lookup or a guess at a code, so each is counted
/// per client address in a fixed window, and the call past the limit is refused with 429, a
/// <c>Retry-After</c> and no body, before it is read, authenticated or checked against Google's keys.
///
/// <b>The address is the connection's.</b> This service trusts no proxy, so <c>X-Forwarded-For</c> and
/// its kin are never read: a header the caller writes cannot choose its own partition. Behind a reverse
/// proxy every caller shares the proxy's address and so one limit; trusting a configured proxy is the
/// follow-up ADR-0009 names. An IPv6 caller is counted by its /64, which is what one subscriber is
/// usually given, so rotating through its own addresses does not buy more calls.
/// </summary>
public static class AnonymousRateLimit
{
    public const string Policy = "anonymous-per-client";

    public static IServiceCollection AddAnonymousRateLimit(this IServiceCollection services, RateLimitOptions limits)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(limits);
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = limits.Window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            // No body: which limit, how many are left and whose partition this is help only somebody
            // tuning a script. Retry-After is all a person's browser needs.
            options.OnRejected = (rejected, _) =>
            {
                var wait = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) && after > TimeSpan.Zero ? after : limits.Window;
                rejected.HttpContext.Response.Headers.RetryAfter = ((long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
        });
    }

    /// <summary>
    /// The partition for a client address: IPv4 as it is, IPv4 carried in IPv6 as the IPv4 it is, IPv6
    /// by its /64. A connection with no address (a Unix socket, a test host) shares one partition.
    /// </summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "none";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
