using System.Text;
using System.Text.Json.Nodes;
using ScreenTail.Core.Update;
using static ScreenTail.Tests.Update.TestManifests;

namespace ScreenTail.Tests.Update;

/// <summary>
/// ST-113 AC2 and ADR-0008: a manifest is believed only when its signature verifies with a compiled-in
/// key, and then only if it names this channel and machine, a newer version, a package on an allowed
/// host, a well-formed SHA-256 and a size within bounds. Every key here is made for the run.
/// </summary>
public sealed class UpdateManifestTests : IDisposable
{
    private readonly TestSigner _signer = new();

    [Fact]
    public void AVerifiedNewerReleaseIsOfferedWithEverythingItSays()
    {
        var verdict = Read(Envelope(_signer, Payload()), _signer.Public);

        Assert.Equal(ManifestOutcome.Offer, verdict.Outcome);
        var manifest = verdict.Manifest!;
        Assert.Equal(UpdateChannel.Stable, manifest.Channel);
        Assert.Equal("x64", manifest.Architecture);
        Assert.Equal(new Version(0, 1, 20), manifest.Version);
        Assert.Equal(new Version(0, 1, 0), manifest.MinimumVersion);
        Assert.Equal(PackageUrl(), manifest.Package.Url);
        Assert.Equal(PackageSha256, manifest.Package.Sha256);
        Assert.Equal(Package.Length, manifest.Package.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), manifest.Published);
    }

    [Fact]
    public void TheVersionAlreadyInstalledIsUpToDate()
    {
        var verdict = Read(Envelope(_signer, Payload("0.1.10")), _signer.Public);

        Assert.Equal(ManifestOutcome.UpToDate, verdict.Outcome);
        Assert.NotNull(verdict.Manifest);
    }

    [Fact]
    public void AnOlderVersionIsRefusedAsADowngrade()
    {
        // Validly signed, and still refused: an old release is a release with old bugs, and serving one
        // is how an attacker who holds the feed but not the key would roll a fleet back.
        var verdict = Read(Envelope(_signer, Payload("0.1.9")), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
        Assert.Null(verdict.Manifest);
        Assert.Contains("older", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AReleaseOlderThanOneAlreadySeenIsRefused()
    {
        // Newer than the installed version but older than one this installation already verified: an old
        // manifest replayed to hold the fleet on a release with a known bug.
        var verdict = Read(Envelope(_signer, Payload("0.1.20")), _signer.Public, newestSeen: new Version(0, 1, 25));

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public void AnInstallationOlderThanTheMinimumIsRefused()
    {
        var verdict = Read(Envelope(_signer, Payload(edit: p => p["minimumVersion"] = "0.1.15")), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
        Assert.Contains("reinstall", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APayloadChangedAfterSigningIsRefused()
    {
        var original = Encoding.UTF8.GetBytes(Payload().ToJsonString());
        var signature = _signer.Sign(original);
        var changed = Encoding.UTF8.GetBytes(Payload(edit: p => p["package"]!["sha256"] = new string('0', 64)).ToJsonString());

        var verdict = Read(Envelope(changed, signature, _signer.Id), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
        Assert.Contains("signature", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignatureByAKeyThisClientDoesNotTrustIsRefusedEvenUnderATrustedKeysName()
    {
        using var stranger = new TestSigner(_signer.Id);

        var verdict = Read(Envelope(stranger, Payload()), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
        Assert.Contains("signature", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyIdThisClientDoesNotKnowIsRefused()
    {
        var verdict = Read(Envelope(_signer, Payload(), keyId: "somebody-else"), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(72)]
    public void ASignatureOfAnyOtherLengthIsRefused(int length)
    {
        // 72 is about the size of the DER form, which this format does not use: one encoding, read one way.
        var verdict = Read(Envelope(_signer, Payload(), sign: _ => new byte[length]), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public void TheSameSignatureInDerFormIsRefused()
    {
        var bytes = Encoding.UTF8.GetBytes(Payload().ToJsonString());
        // Signed in that form by the runtime itself. Re-encoding r and s by hand failed whenever either had
        // a leading zero byte, about one run in a hundred (2026-10-29 review).
        var der = _signer.SignDer(bytes);

        var verdict = Read(Envelope(bytes, der, _signer.Id), _signer.Public);

        Assert.NotEqual(64, der.Length);
        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
    }

    public static TheoryData<string, Action<JsonObject>> SignedButWrong() => new()
    {
        { "another channel", p => p["channel"] = "pilot" },
        { "an unknown channel", p => p["channel"] = "beta" },
        { "another architecture", p => p["architecture"] = "arm64" },
        { "plain http", p => p["package"]!["url"] = $"http://{PackageHost}/ScreenTail.msi" },
        { "a host not on the list", p => p["package"]!["url"] = "https://downloads.attacker.example/ScreenTail.msi" },
        { "a host that only ends like ours", p => p["package"]!["url"] = $"https://evil{PackageHost}/ScreenTail.msi" },
        { "credentials in the URL", p => p["package"]!["url"] = $"https://user:pass@{PackageHost}/ScreenTail.msi" },
        { "another port", p => p["package"]!["url"] = $"https://{PackageHost}:8443/ScreenTail.msi" },
        { "a relative URL", p => p["package"]!["url"] = "/ScreenTail.msi" },
        { "an uppercase hash", p => p["package"]!["sha256"] = PackageSha256.ToUpperInvariant() },
        { "a short hash", p => p["package"]!["sha256"] = PackageSha256[..40] },
        { "a hash that is not hex", p => p["package"]!["sha256"] = new string('g', 64) },
        { "a size of zero", p => p["package"]!["size"] = 0 },
        { "a negative size", p => p["package"]!["size"] = -1 },
        { "a size too large to be ours", p => p["package"]!["size"] = UpdateManifestReader.MaxPackageBytes + 1 },
        { "a size written as text", p => p["package"]!["size"] = "4096" },
        { "a fractional size", p => p["package"]!["size"] = 4096.5 },
        { "no package", p => p.Remove("package") },
        { "no version", p => p.Remove("version") },
        { "no minimum version", p => p.Remove("minimumVersion") },
        { "no channel", p => p.Remove("channel") },
        { "a two-part version", p => p["version"] = "0.2" },
        { "a version with a prefix", p => p["version"] = "v0.1.20" },
        { "a version with a leading zero", p => p["version"] = "0.1.020" },
        { "a version written as a number", p => p["version"] = 20 },
        { "another payload format", p => p["format"] = "screentail-update-v2" },
        { "no publication time", p => p.Remove("published") },
    };

    [Theory]
    [MemberData(nameof(SignedButWrong))]
    public void ASignedManifestThatIsWrongForThisInstallationIsRefused(string what, Action<JsonObject> edit)
    {
        // Signed with the trusted key, so each of these is a refusal of the content and not of the
        // signature: the checks after the signature are checks in their own right.
        var verdict = Read(Envelope(_signer, Payload(edit: edit)), _signer.Public);

        Assert.True(verdict.Outcome == ManifestOutcome.Refused, $"{what} was accepted");
        Assert.Null(verdict.Manifest);
    }

    [Theory]
    [InlineData("stable", UpdateChannel.Pilot)]
    [InlineData("pilot", UpdateChannel.Stable)]
    public void AManifestSignedForOneChannelIsRefusedOnTheOther(string signedFor, UpdateChannel device)
    {
        // The channel is inside the signed bytes, so a pilot release cannot be served to stable devices
        // by copying its manifest to the stable path, nor the other way round.
        var verdict = Read(Envelope(_signer, Payload(edit: p => p["channel"] = signedFor)), _signer.Public, device);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
        Assert.Equal(UpdateManifestReader.WrongChannel, verdict.Reason);
    }

    [Fact]
    public void APilotManifestIsReadOnThePilotChannel()
    {
        var verdict = Read(Envelope(_signer, Payload(edit: p => p["channel"] = "pilot")), _signer.Public, UpdateChannel.Pilot);

        Assert.Equal(ManifestOutcome.Offer, verdict.Outcome);
    }

    public static TheoryData<string, byte[]> NotAnEnvelope() => new()
    {
        { "nothing", [] },
        { "not JSON", Encoding.UTF8.GetBytes("<html>hello</html>") },
        { "a JSON array", Encoding.UTF8.GetBytes("[1,2,3]") },
        { "no signature", Encoding.UTF8.GetBytes("""{"format":"screentail-update-manifest-v1","key":"test-key","payload":"e30="}""") },
        { "a payload that is not base64", Encoding.UTF8.GetBytes("""{"format":"screentail-update-manifest-v1","key":"test-key","payload":"%%%","signature":"AAAA"}""") },
        { "too large to be a manifest", new byte[UpdateManifestReader.MaxBytes + 1] },
    };

    [Theory]
    [MemberData(nameof(NotAnEnvelope))]
    public void AnythingThatIsNotAnEnvelopeIsRefused(string what, byte[] body)
    {
        var verdict = Read(body, _signer.Public);

        Assert.True(verdict.Outcome == ManifestOutcome.Refused, $"{what} was accepted");
    }

    [Fact]
    public void AnEnvelopeOfAnotherFormatIsRefused()
    {
        var verdict = Read(Envelope(_signer, Payload(), format: "screentail-update-manifest-v2"), _signer.Public);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
    }

    [Fact]
    public void ARefusalNeverQuotesTheManifest()
    {
        // The reason goes to the log and to Settings, and a manifest that failed its checks is anybody's
        // text (INV-10). A fixed sentence of ours, whatever the manifest said.
        var hostile = Payload("9.9.9", p => p["package"]!["url"] = "https://downloads.attacker.example/please-log-me");

        var signedWrong = Read(Envelope(_signer, hostile), _signer.Public);
        var unsigned = Read(Envelope(_signer, hostile, sign: _ => new byte[64]), _signer.Public);

        foreach (var verdict in new[] { signedWrong, unsigned })
        {
            Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
            Assert.DoesNotContain("attacker", verdict.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("9.9.9", verdict.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithNoTrustedKeyNothingIsBelieved()
    {
        var verdict = UpdateManifestReader.Read(Envelope(_signer, Payload()), Trust(), UpdateChannel.Stable, "x64", Installed);

        Assert.Equal(ManifestOutcome.Refused, verdict.Outcome);
    }

    public void Dispose() => _signer.Dispose();
}
