using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ScreenTail.Api.Auth;

/// <summary>
/// The reverse proxies this deployment sits behind, if any (ST-099 hardening, ADR-0009). Empty by
/// default, and empty means no forwarded header is read from anybody: the client's address is the
/// connection's, and a caller cannot choose its own rate-limit partition by writing X-Forwarded-For.
///
/// With a proxy listed, X-Forwarded-For and X-Forwarded-Proto are honoured only on a connection whose
/// peer is one of these addresses or inside one of these networks, and only the last
/// <see cref="ForwardLimit"/> entries are read (by default one: the address the trusted proxy itself
/// appended). ASP.NET Core's own default, which trusts loopback, is cleared: nothing is trusted that is
/// not written here.
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string Section = "ForwardedHeaders";

    /// <summary>Single proxy addresses, such as <c>10.0.0.5</c> or <c>2001:db8::5</c>.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Proxy networks in CIDR form, such as <c>10.0.0.0/24</c>.</summary>
    public IList<string> KnownNetworks { get; } = [];

    /// <summary>How many proxies in a row are trusted, counted from the API back. Each must be listed above.</summary>
    public int ForwardLimit { get; set; } = 1;

    public bool Configured => KnownProxies.Any(p => !string.IsNullOrWhiteSpace(p)) || KnownNetworks.Any(n => !string.IsNullOrWhiteSpace(n));

    /// <summary>
    /// The configuration key ASP.NET Core reads <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> as. With it on,
    /// the framework runs the forwarded-headers middleware itself, ahead of ours.
    /// </summary>
    public const string FrameworkSwitch = "FORWARDEDHEADERS_ENABLED";

    /// <summary>
    /// Refuses, at startup, an address or network that does not parse, rather than trusting less or more
    /// than was meant. And, with a proxy listed, the framework's own switch (<see cref="FrameworkSwitch"/>,
    /// passed in as configured) on as well: the forwarded headers would then be read twice, and the
    /// second pass treats the address the first one found as a peer, so a client inside a listed network
    /// could choose its own address (ADR-0009, ST-007).
    /// </summary>
    public void Validate() => Validate(frameworkSwitch: null);

    /// <inheritdoc cref="Validate()"/>
    public void Validate(string? frameworkSwitch)
    {
        if (Configured && bool.TryParse(frameworkSwitch?.Trim(), out var on) && on)
        {
            throw new InvalidOperationException(
                $"ASPNETCORE_FORWARDEDHEADERS_ENABLED is on and {Section}:KnownProxies or KnownNetworks lists a proxy. "
                + "Leave the variable unset: the listed proxies are all the service needs, and with both on the forwarded headers are read twice.");
        }

        foreach (var proxy in KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!IPAddress.TryParse(proxy.Trim(), out _))
            {
                throw new InvalidOperationException($"{Section}:KnownProxies holds '{proxy}', which is not an IP address.");
            }
        }

        foreach (var network in KnownNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (!System.Net.IPNetwork.TryParse(network.Trim(), out _))
            {
                throw new InvalidOperationException($"{Section}:KnownNetworks holds '{network}', which is not a network in CIDR form (10.0.0.0/24).");
            }
        }

        if (ForwardLimit is < 1 or > 5)
        {
            throw new InvalidOperationException($"{Section}:ForwardLimit must be between 1 and 5.");
        }
    }

    /// <summary>
    /// The middleware's options: only these proxies, only the client address and scheme, and with none
    /// listed, nothing at all. Applied after every other configuration (PostConfigure), so it also
    /// overrides ASPNETCORE_FORWARDEDHEADERS_ENABLED, with which the framework runs the middleware itself
    /// and trusts every caller's X-Forwarded-For: empty trust lists mean "anybody" to the middleware.
    /// </summary>
    public void Apply(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        if (!Configured)
        {
            options.ForwardedHeaders = ForwardedHeaders.None;
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = ForwardLimit;
        options.RequireHeaderSymmetry = false;
        foreach (var proxy in KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
        }

        foreach (var network in KnownNetworks.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network.Trim()));
        }
    }
}
