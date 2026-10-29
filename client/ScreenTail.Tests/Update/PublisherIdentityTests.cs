using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Update;

/// <summary>
/// "The same publisher" by identity, not by leaf certificate (ADR-0007's seam for ST-113). Artifact
/// Signing rotates its leaf every few days, so these build certificates at run time that share or do not
/// share a subject and an identity-validation EKU, with different keys and so different thumbprints.
/// That the chain is trusted is Windows' half, held by the Windows suite.
/// </summary>
public sealed class PublisherIdentityTests
{
    private const string Ours = "CN=ScreenTail Test Publisher, O=ScreenTail Test Publisher, L=Bloomington, S=Illinois, C=US";
    private const string Validation = PublisherIdentity.ArtifactSigningOidPrefix + "1.12345.1";

    [Fact]
    public void ANewLeafForTheSamePublisherIsTheSamePublisher()
    {
        using var today = Certificate(Ours, Validation);
        using var nextWeek = Certificate(Ours, Validation);
        var running = PublisherIdentity.From(today, timestamped: true);
        var package = PublisherIdentity.From(nextWeek, timestamped: true);

        Assert.NotEqual(running.Thumbprint, package.Thumbprint);
        Assert.Null(PublisherIdentity.Mismatch(running, package));
    }

    [Fact]
    public void AnotherSubjectIsAnotherPublisher()
    {
        using var ours = Certificate(Ours, Validation);
        using var theirs = Certificate("CN=Somebody Else, O=Somebody Else, C=US", Validation);

        Assert.NotNull(PublisherIdentity.Mismatch(PublisherIdentity.From(ours, true), PublisherIdentity.From(theirs, true)));
    }

    [Fact]
    public void TheSameSubjectUnderAnotherIdentityValidationIsAnotherPublisher()
    {
        // Artifact Signing writes the identity validation into every certificate it issues. A subject
        // string can be matched by another validated organisation of the same name; the validation's OID
        // cannot.
        using var ours = Certificate(Ours, Validation);
        using var lookalike = Certificate(Ours, PublisherIdentity.ArtifactSigningOidPrefix + "1.99999.1");
        using var none = Certificate(Ours);

        Assert.NotNull(PublisherIdentity.Mismatch(PublisherIdentity.From(ours, true), PublisherIdentity.From(lookalike, true)));
        Assert.NotNull(PublisherIdentity.Mismatch(PublisherIdentity.From(ours, true), PublisherIdentity.From(none, true)));
    }

    [Fact]
    public void WithoutAValidationOidTheSubjectDecides()
    {
        // A certificate from the Windows store (a hardware token) carries no Artifact Signing OID.
        using var first = Certificate(Ours);
        using var renewed = Certificate(Ours);

        Assert.Null(PublisherIdentity.Mismatch(PublisherIdentity.From(first, true), PublisherIdentity.From(renewed, true)));
    }

    [Fact]
    public void APackageSignedWithoutATimestampIsRefused()
    {
        // A short-lived certificate without a timestamp stops verifying within days; a package signed
        // that way was not signed by the release pipeline (sign.ps1 refuses to make one).
        using var today = Certificate(Ours, Validation);
        using var package = Certificate(Ours, Validation);

        var reason = PublisherIdentity.Mismatch(PublisherIdentity.From(today, true), PublisherIdentity.From(package, timestamped: false));

        Assert.NotNull(reason);
        Assert.Contains("timestamp", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADevelopmentBuildOrAnUnacceptedPackageHasNoPublisherToCompare()
    {
        using var ours = Certificate(Ours, Validation);
        var identity = PublisherIdentity.From(ours, true);

        Assert.Contains("development build", PublisherIdentity.Mismatch(null, identity), StringComparison.Ordinal);
        Assert.NotNull(PublisherIdentity.Mismatch(identity, null));
    }

    [Fact]
    public void OnlyTheIdentityValidationUsagesAreRead()
    {
        using var certificate = Certificate(Ours, Validation, "1.3.6.1.5.5.7.3.3");

        Assert.Equal([Validation], PublisherIdentity.From(certificate, true).ValidationOids);
    }

    private static X509Certificate2 Certificate(string subject, params string[] usages)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        if (usages.Length > 0)
        {
            var oids = new OidCollection();
            foreach (var usage in usages)
            {
                _ = oids.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
    }
}
