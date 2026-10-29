using System.Text.Json;

namespace ScreenTail.Core.Update;

/// <summary>An ECDSA P-256 public key a manifest may be signed with, and the id a manifest names it by.</summary>
public sealed record UpdateKey(string Id, byte[] SubjectPublicKeyInfo);

/// <summary>
/// Where updates come from and whose signature makes one real (ST-113, ADR-0008).
///
/// Compiled in, from <c>shared/release/update-trust.json</c>, rather than read from anything on the
/// machine: the feed, the hosts a package may come from and the public keys are what every other check
/// rests on, and a file beside the service in the user's profile is a file the user (or anything running
/// as them) can rewrite. The same file is what CI's signing job checks each manifest against, so the
/// key the client trusts and the key the release is signed with cannot drift apart unnoticed.
///
/// <b>Unconfigured is the shipped state</b> until the owner creates the key and the hosting, and an
/// unconfigured build makes no update request at all.
/// </summary>
public sealed record UpdateTrust(Uri? Feed, IReadOnlySet<string> PackageHosts, IReadOnlyList<UpdateKey> Keys)
{
    private static readonly Lazy<UpdateTrust> Embedded = new(LoadEmbedded);

    /// <summary>What this build was compiled with.</summary>
    public static UpdateTrust Shipped => Embedded.Value;

    /// <summary>Nothing to check against. Used when the embedded file is missing or will not parse.</summary>
    public static UpdateTrust None { get; } = new(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>A feed, at least one package host and at least one key: everything a check needs.</summary>
    public bool Configured => Feed is not null && PackageHosts.Count > 0 && Keys.Count > 0;

    /// <summary>Every host an update request may reach: the feed's and the packages'. The egress guard's list for <c>UpdateCheck</c>.</summary>
    public IReadOnlySet<string> Hosts
    {
        get
        {
            var hosts = new HashSet<string>(PackageHosts, StringComparer.OrdinalIgnoreCase);
            if (Feed is not null)
            {
                _ = hosts.Add(Feed.Host);
            }

            return hosts;
        }
    }

    /// <summary>The one URL a check asks for. The channel and the architecture are in the path, and nothing else is sent.</summary>
    public Uri ManifestFor(UpdateChannel channel, string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        if (Feed is null)
        {
            throw new InvalidOperationException("No update feed is configured.");
        }

        return new Uri(Feed, $"{channel.ToWire()}/win-{architecture}.json");
    }

    /// <summary>
    /// Reads the trust file. Throws <see cref="FormatException"/> for anything that is not exactly its
    /// shape: a feed that is not HTTPS, a host with a wildcard, a key that is not P-256.
    /// </summary>
    public static UpdateTrust Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Uri? feed = null;
            var feedElement = root.GetProperty("feed");
            if (feedElement.ValueKind == JsonValueKind.String)
            {
                feed = FeedUri(feedElement.GetString()!);
            }
            else if (feedElement.ValueKind != JsonValueKind.Null)
            {
                throw new FormatException("feed must be a string or null.");
            }

            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in root.GetProperty("packageHosts").EnumerateArray())
            {
                var name = host.GetString() ?? throw new FormatException("A package host must be a string.");
                if (Uri.CheckHostName(name) != UriHostNameType.Dns || name.Contains('*', StringComparison.Ordinal))
                {
                    throw new FormatException("A package host must be one exact host name.");
                }

                _ = hosts.Add(name);
            }

            var keys = new List<UpdateKey>();
            foreach (var key in root.GetProperty("keys").EnumerateArray())
            {
                var id = key.GetProperty("id").GetString();
                var spki = key.GetProperty("spki").GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(spki))
                {
                    throw new FormatException("A key needs an id and a public key.");
                }

                var bytes = Convert.FromBase64String(spki);
                ManifestSignature.ImportP256(bytes).Dispose();
                keys.Add(new UpdateKey(id, bytes));
            }

            return new UpdateTrust(feed, hosts, keys);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            throw new FormatException("The update trust file is not in its expected shape.", ex);
        }
    }

    private static Uri FeedUri(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var feed)
            || feed.Scheme != Uri.UriSchemeHttps
            || !feed.IsDefaultPort
            || feed.UserInfo.Length > 0
            || feed.Query.Length > 0
            || feed.Fragment.Length > 0
            || !feed.AbsolutePath.EndsWith('/'))
        {
            throw new FormatException("The feed must be an https folder URL ending in a slash, with no query.");
        }

        return feed;
    }

    private static UpdateTrust LoadEmbedded()
    {
        using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("ScreenTail.Core.Update.update-trust.json");
        if (stream is null)
        {
            return None;
        }

        using var reader = new StreamReader(stream);
        try
        {
            return Parse(reader.ReadToEnd());
        }
        catch (FormatException)
        {
            // A build whose trust file does not parse checks for nothing, the same as an unconfigured one.
            return None;
        }
    }
}
