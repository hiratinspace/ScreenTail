using System.Net;
using System.Net.Sockets;

namespace ScreenTail.Api.Providers;

/// <summary>
/// The rule for where a PSA or documentation call may go: public internet addresses only (security check
/// 2026-10-14). A tenant stores its provider's site and the backend calls it, so without this a site
/// could name loopback, a private range, a link-local metadata address or a service beside the backend,
/// and the provider's error path would hand back a line of whatever answered.
///
/// The check runs when the connection is made, not only when the site is stored: <see cref="Handler"/>
/// resolves the name itself, refuses unless every address is public, and connects to the addresses it
/// checked. A name that answers with a public address for a check and a private one a moment later (DNS
/// rebinding) gets no second lookup to swap in. <see cref="IsPlainlyPrivate"/> is the store-time half,
/// which only exists to say so early and in words.
/// </summary>
public static class PublicNetwork
{
    /// <summary>IPv4 ranges that are not the public internet, as (network, prefix length).</summary>
    private static readonly (uint Network, int Prefix)[] NotPublicV4 =
    [
        (0x00000000, 8),  // 0.0.0.0/8, "this network", and the unspecified address
        (0x0A000000, 8),  // 10.0.0.0/8, private
        (0x64400000, 10), // 100.64.0.0/10, carrier-grade NAT
        (0x7F000000, 8),  // 127.0.0.0/8, loopback
        (0xA9FE0000, 16), // 169.254.0.0/16, link-local, where cloud metadata lives
        (0xAC100000, 12), // 172.16.0.0/12, private
        (0xC0000000, 24), // 192.0.0.0/24, protocol assignments
        (0xC0000200, 24), // 192.0.2.0/24, documentation
        (0xC0586300, 24), // 192.88.99.0/24, the old 6to4 relay
        (0xC0A80000, 16), // 192.168.0.0/16, private
        (0xC6120000, 15), // 198.18.0.0/15, benchmarking
        (0xC6336400, 24), // 198.51.100.0/24, documentation
        (0xCB007100, 24), // 203.0.113.0/24, documentation
        (0xE0000000, 4),  // 224.0.0.0/4, multicast
        (0xF0000000, 4),  // 240.0.0.0/4, reserved, and the broadcast address
    ];

    /// <summary>
    /// True only for an address on the public internet. IPv4 outside the ranges above; IPv6 inside global
    /// unicast (2000::/3) and outside its documentation and protocol ranges. An IPv6 form that carries an
    /// IPv4 address (mapped, NAT64, 6to4) is judged by the IPv4 address it carries.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPublicV4(b);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicV4(b.AsSpan(12, 4));
        }

        // 64:ff9b::/96, the well-known NAT64 prefix: the IPv4 address is the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublicV4(b.AsSpan(12, 4));
        }

        // Outside 2000::/3 nothing is public: the unspecified and loopback addresses, the IPv4-compatible
        // ::/96, unique-local fc00::/7, link-local fe80::/10, site-local fec0::/10, multicast ff00::/8.
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        return (b[0], b[1]) switch
        {
            (0x20, 0x01) when b[2] <= 0x01 => false,                  // 2001::/23, protocol assignments (Teredo among them)
            (0x20, 0x01) when b[2] == 0x0d && b[3] == 0xb8 => false,  // 2001:db8::/32, documentation
            (0x20, 0x02) => IsPublicV4(b.AsSpan(2, 4)),               // 2002::/16, 6to4, the IPv4 address in bytes 2 to 5
            (0x3f, 0xff) when b[2] < 0x10 => false,                   // 3fff::/20, documentation
            _ => true,
        };
    }

    /// <summary>
    /// A site that is plainly not on the public internet, judged without a lookup: <c>localhost</c>, a
    /// name under <c>.localhost</c>, or an address literal that is not public. A name that resolves to a
    /// private address passes here and is refused by <see cref="Handler"/> when the call connects.
    /// </summary>
    public static bool IsPlainlyPrivate(Uri site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var host = site.IdnHost.Trim('[', ']').TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var literal) && !IsPublic(literal);
    }

    /// <summary>
    /// The handler for the PSA and documentation clients. Every connection, including any a later request
    /// on the same client opens, goes through <see cref="ConnectAsync"/>. No proxy, because a proxy would
    /// resolve the name itself and the check would be of the proxy; no redirects, because a redirect would
    /// carry a provider key header to whatever host it names; no cookies, because the handler is shared
    /// by every tenant's client.
    /// </summary>
    /// <param name="allowPrivate">Development and tests only (<see cref="ProviderNetworkOptions"/>).</param>
    /// <param name="resolve">The name lookup; the system resolver unless a test supplies one.</param>
    public static SocketsHttpHandler Handler(bool allowPrivate, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var lookup = resolve ?? Dns.GetHostAddressesAsync;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, allowPrivate, lookup, ct),
        };
    }

    /// <summary>Whether a failed send was this rule refusing the destination, however the handler wrapped it.</summary>
    public static bool WasRefused(Exception? failure)
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is PrivateAddressRefusedException)
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var host = endpoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await resolve(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // Every address, not the first: a name that answers with one public and one private address is
        // refused outright rather than trusted to be connected in the order it was given.
        if (!allowPrivate && !Array.TrueForAll(addresses, IsPublic))
        {
            throw new PrivateAddressRefusedException();
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsPublicV4(ReadOnlySpan<byte> b)
    {
        var value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        foreach (var (network, prefix) in NotPublicV4)
        {
            var mask = uint.MaxValue << (32 - prefix);
            if ((value & mask) == network)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A provider call refused before it connected, because its site resolved to an address that is not public.</summary>
public sealed class PrivateAddressRefusedException : Exception
{
    public PrivateAddressRefusedException()
        : base("The provider's site resolves to an address that is not on the public internet, so it is not called.")
    {
    }

    public PrivateAddressRefusedException(string message)
        : base(message)
    {
    }

    public PrivateAddressRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
