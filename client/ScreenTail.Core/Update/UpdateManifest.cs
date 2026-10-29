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

    public static ManifestVerdict Read(
        ReadOnlySpan<byte> envelope,
        UpdateTrust trust,
        UpdateChannel channel,
        string architecture,
        Version installed,
        Version? newestSeen = null)
    {
        throw new NotImplementedException();
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

    internal static JsonDocument? TryParse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8 });
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
