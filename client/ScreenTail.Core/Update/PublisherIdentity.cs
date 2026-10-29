using System.Security.Cryptography.X509Certificates;

namespace ScreenTail.Core.Update;

/// <summary>
/// Who signed a file, as far as deciding "the same publisher" needs (ST-113, ADR-0007's seam).
///
/// <b>Not the leaf thumbprint.</b> Azure Artifact Signing issues a new short-lived certificate every few
/// days, so next week's release is signed by a different leaf from today's installation, and a
/// thumbprint comparison would refuse every update. What stays the same across those certificates is the
/// publisher's validated identity: the certificate's subject, and the identity-validation policy Artifact
/// Signing writes into each certificate's extended key usage (an OID under
/// <see cref="ArtifactSigningOidPrefix"/>). Both are compared; the thumbprint is kept for the log only.
///
/// A subject is just text until Windows has walked the chain to a root it trusts, so a
/// <see cref="PublisherIdentity"/> is only ever built after that check has passed (the Windows reader
/// returns null otherwise). That is what stops a self-signed certificate with our subject passing.
/// </summary>
/// <param name="Subject">The signing certificate's subject, as the certificate writes it.</param>
/// <param name="ValidationOids">The certificate's extended key usages under <see cref="ArtifactSigningOidPrefix"/>.</param>
/// <param name="Thumbprint">The leaf's thumbprint. For the log; never compared.</param>
/// <param name="Timestamped">The signature carries a timestamp, so it outlives its short-lived certificate.</param>
public sealed record PublisherIdentity(string Subject, IReadOnlySet<string> ValidationOids, string Thumbprint, bool Timestamped)
{
    /// <summary>Microsoft's arc for Artifact Signing's identity-validation EKUs.</summary>
    public const string ArtifactSigningOidPrefix = "1.3.6.1.4.1.311.97.";

    public static PublisherIdentity From(X509Certificate2 signer, bool timestamped)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var oids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in signer.Extensions.OfType<X509EnhancedKeyUsageExtension>())
        {
            foreach (var usage in extension.EnhancedKeyUsages)
            {
                if (usage.Value is { } value && value.StartsWith(ArtifactSigningOidPrefix, StringComparison.Ordinal))
                {
                    _ = oids.Add(value);
                }
            }
        }

        return new PublisherIdentity(signer.Subject, oids, signer.Thumbprint, timestamped);
    }

    /// <summary>
    /// Null when <paramref name="package"/> was signed by the publisher of <paramref name="running"/>;
    /// otherwise why not, in a sentence for the technician and the log.
    /// </summary>
    /// <param name="running">The running service's signer. Null for a build nobody signed.</param>
    /// <param name="package">The downloaded package's signer, null when Windows did not accept its signature.</param>
    public static string? Mismatch(PublisherIdentity? running, PublisherIdentity? package)
    {
        if (running is null)
        {
            return "This is a development build, so it does not install updates: there is no publisher to compare the package with.";
        }

        if (package is null)
        {
            return "The downloaded package is not signed in a way Windows accepts, so it was not installed.";
        }

        if (!package.Timestamped)
        {
            return "The downloaded package's signature has no timestamp, so it was not signed by ScreenTail's release process.";
        }

        // The identity, never the leaf: a new certificate from the same validated publisher is the same
        // publisher. Where the running build carries an identity-validation OID, the package must carry
        // exactly the same ones, so a same-named organisation validated separately is someone else.
        var samePublisher = string.Equals(running.Subject, package.Subject, StringComparison.Ordinal)
            && (running.ValidationOids.Count == 0 || running.ValidationOids.SetEquals(package.ValidationOids));
        return samePublisher
            ? null
            : "The downloaded package was signed by someone other than this installation's publisher, so it was not installed.";
    }
}
