using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenTail.Core.Update;

/// <param name="Url">HTTPS, on one of the trust file's package hosts.</param>
/// <param name="Sha256">Lowercase hex, 64 characters.</param>
/// <param name="Size">Bytes, exactly.</param>
public sealed record UpdatePackage(Uri Url, string Sha256, long Size);

/// <summary>
/// One release, as a signed manifest states it (ST-113, ADR-0008).
/// </summary>
/// <param name="MinimumVersion">
/// The oldest installed version that may update itself straight to this one. Older installations are told
/// to be reinstalled instead, so a release that cannot carry an old store forward never has to.
/// </param>
public sealed record UpdateManifest(
    UpdateChannel Channel,
    string Architecture,
    Version Version,
    Version MinimumVersion,
    UpdatePackage Package,
    DateTimeOffset Published);

public enum ManifestOutcome
{
    /// <summary>A newer release, verified, for this channel and this machine.</summary>
    Offer,

    /// <summary>Verified, and not newer than what is installed.</summary>
    UpToDate,

    /// <summary>Not to be believed or not to be installed. The reason says which, in our own words.</summary>
    Refused,
}

/// <param name="Manifest">Set for an offer and for up to date; never for a refusal, whose contents are not to be trusted.</param>
/// <param name="Reason">A fixed sentence of ours. It never quotes the manifest, which may be anybody's (INV-10).</param>
public sealed record ManifestVerdict(ManifestOutcome Outcome, UpdateManifest? Manifest, string Reason)
{
    public static ManifestVerdict Refuse(string reason) => new(ManifestOutcome.Refused, null, reason);
}

/// <summary>
/// Reads an update manifest and decides what it means for this installation (ST-113, ADR-0008).
///
/// The manifest on the wire is an envelope: the format, the id of the key that signed it, the signed
/// bytes (base64) and an ECDSA P-256 SHA-256 signature over exactly those bytes (base64, r then s,
/// 64 bytes). Signing bytes rather than a re-serialised object means there is no canonical form to get
/// wrong: what was signed is what is parsed.
///
/// <b>The order is the point.</b> Nothing inside the payload is read until the signature over it has
/// verified with a key compiled into this client. Only then is it held to this installation: its
/// channel, its architecture, a version newer than the installed one and no older than the newest
/// already seen, an installed version no older than its minimum, a package URL on an allowed host, a
/// well-formed SHA-256 and a size within bounds. Any one failing refuses the whole manifest.
/// </summary>
public static class UpdateManifestReader
{
    /// <summary>A manifest is a few hundred bytes. Anything near this is not one.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The MSI is under 100 MB (ST-112); five times that is not a ScreenTail package.</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    public const string EnvelopeFormat = "screentail-update-manifest-v1";

    public const string PayloadFormat = "screentail-update-v1";

    // The sentences a refusal gives. Ours, fixed, and never built from what the manifest said (INV-10).
    public const string NotAManifest = "The update feed answered with something that is not an update manifest.";
    public const string UnknownKey = "The update manifest names a signing key this version of ScreenTail does not trust.";
    public const string BadSignature = "The update manifest's signature does not verify, so it was not believed.";
    public const string Malformed = "The update manifest is signed but not in the shape this version reads.";
    public const string WrongChannel = "The update manifest is for another channel.";
    public const string WrongArchitecture = "The update manifest is for another kind of processor.";
    public const string PackageHostNotAllowed = "The update manifest's package is not on a host this version of ScreenTail downloads from.";
    public const string Downgrade = "The update manifest offers a release older than the one installed.";
    public const string Rollback = "The update manifest is older than one this installation has already seen.";
    public const string TooOld = "This installation is too old to update itself to the new release; reinstall ScreenTail with your MSP's package.";

    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    public static ManifestVerdict Read(
        ReadOnlySpan<byte> envelope,
        UpdateTrust trust,
        UpdateChannel channel,
        string architecture,
        Version installed,
        Version? newestSeen = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        ArgumentNullException.ThrowIfNull(installed);
        if (envelope.IsEmpty || envelope.Length > MaxBytes)
        {
            return ManifestVerdict.Refuse(NotAManifest);
        }

        byte[] signedBytes;
        using (var document = Parse(envelope))
        {
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ManifestVerdict.Refuse(NotAManifest);
            }

            var root = document.RootElement;
            if (!Text(root, "format", out var format) || format != EnvelopeFormat
                || !Text(root, "key", out var keyId)
                || !Text(root, "payload", out var payload)
                || !Text(root, "signature", out var signatureText))
            {
                return ManifestVerdict.Refuse(NotAManifest);
            }

            var key = trust.Keys.FirstOrDefault(k => string.Equals(k.Id, keyId, StringComparison.Ordinal));
            if (key is null)
            {
                return ManifestVerdict.Refuse(UnknownKey);
            }

            byte[] signature;
            try
            {
                signedBytes = Convert.FromBase64String(payload);
                signature = Convert.FromBase64String(signatureText);
            }
            catch (FormatException)
            {
                return ManifestVerdict.Refuse(NotAManifest);
            }

            // The signature first, and nothing inside the payload before it.
            if (!ManifestSignature.Verifies(key, signedBytes, signature))
            {
                return ManifestVerdict.Refuse(BadSignature);
            }
        }

        return Judge(signedBytes, trust, channel, architecture, installed, newestSeen);
    }

    /// <summary>The signed bytes, now known to be ours, held to this installation.</summary>
    private static ManifestVerdict Judge(byte[] signedBytes, UpdateTrust trust, UpdateChannel channel, string architecture, Version installed, Version? newestSeen)
    {
        using var document = Parse(signedBytes);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return ManifestVerdict.Refuse(Malformed);
        }

        var root = document.RootElement;
        if (!Text(root, "format", out var format) || format != PayloadFormat
            || !Text(root, "channel", out var channelText)
            || !Text(root, "architecture", out var architectureText)
            || !Text(root, "version", out var versionText) || !ReleaseVersion.TryParse(versionText, out var version)
            || !Text(root, "minimumVersion", out var minimumText) || !ReleaseVersion.TryParse(minimumText, out var minimum)
            || !root.TryGetProperty("published", out var publishedElement) || publishedElement.ValueKind != JsonValueKind.String
            || !publishedElement.TryGetDateTimeOffset(out var published)
            || !root.TryGetProperty("package", out var packageElement) || packageElement.ValueKind != JsonValueKind.Object
            || !Text(packageElement, "url", out var urlText)
            || !Text(packageElement, "sha256", out var sha256)
            || !packageElement.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size)
            || minimum > version)
        {
            return ManifestVerdict.Refuse(Malformed);
        }

        if (!UpdateChannels.TryParse(channelText, out var manifestChannel) || manifestChannel != channel)
        {
            return ManifestVerdict.Refuse(WrongChannel);
        }

        if (!string.Equals(architectureText, architecture, StringComparison.Ordinal))
        {
            return ManifestVerdict.Refuse(WrongArchitecture);
        }

        if (!IsSha256(sha256) || size < 1 || size > MaxPackageBytes)
        {
            return ManifestVerdict.Refuse(Malformed);
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !url.IsDefaultPort
            || url.UserInfo.Length > 0
            || url.Fragment.Length > 0
            || !trust.PackageHosts.Contains(url.Host))
        {
            return ManifestVerdict.Refuse(PackageHostNotAllowed);
        }

        if (version < installed)
        {
            return ManifestVerdict.Refuse(Downgrade);
        }

        if (newestSeen is not null && version < newestSeen)
        {
            return ManifestVerdict.Refuse(Rollback);
        }

        var manifest = new UpdateManifest(manifestChannel, architectureText, version, minimum, new UpdatePackage(url, sha256, size), published);
        if (version == installed)
        {
            return new ManifestVerdict(ManifestOutcome.UpToDate, manifest, "Up to date.");
        }

        return installed < minimum
            ? ManifestVerdict.Refuse(TooOld)
            : new ManifestVerdict(ManifestOutcome.Offer, manifest, "A newer release.");
    }

    private static bool IsSha256(string text) =>
        text.Length == 64 && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>A string property, and only a string.</summary>
    private static bool Text(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static JsonDocument? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json.ToArray(), Strict);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>ECDSA P-256 with SHA-256, the one signature an update manifest may carry.</summary>
public static class ManifestSignature
{
    /// <summary>The DER prefix of a P-256 SubjectPublicKeyInfo, before the 65-byte uncompressed point.</summary>
    private static readonly byte[] P256Prefix = Convert.FromHexString("3059301306072a8648ce3d020106082a8648ce3d030107034200");

    /// <summary>A P-256 public key, or a <see cref="CryptographicException"/> for anything else.</summary>
    public static ECDsa ImportP256(byte[] subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);
        if (subjectPublicKeyInfo.Length != P256Prefix.Length + 65 || !subjectPublicKeyInfo.AsSpan(0, P256Prefix.Length).SequenceEqual(P256Prefix))
        {
            throw new CryptographicException("Not a P-256 public key.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>True when <paramref name="signature"/> (r then s, 64 bytes) is <paramref name="key"/>'s over exactly <paramref name="message"/>.</summary>
    public static bool Verifies(UpdateKey key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (signature.Length != 64)
        {
            return false;
        }

        using var ecdsa = ImportP256(key.SubjectPublicKeyInfo);
        return ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
