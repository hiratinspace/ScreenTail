using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ScreenTail.Core.Net;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Update;

/// <summary>
/// The trust file compiled into the client (ST-113): the feed, the package hosts and the manifest keys.
/// Read strictly, because everything else rests on it, and shipped empty until the owner configures it.
/// </summary>
public sealed class UpdateTrustTests
{
    [Fact]
    public void TheShippedBuildChecksForNothingUntilTheOwnerConfiguresIt()
    {
        // shared/release/update-trust.json is empty in the repository. A build made from it must not make
        // an update request to anywhere, and the egress guard must have no update host at all.
        Assert.False(UpdateTrust.Shipped.Configured);
        Assert.Empty(UpdateTrust.Shipped.Hosts);
        Assert.Empty(ClientEgress.Settings(new Uri("https://api.screentail.example/")).UpdateHosts);
    }

    [Fact]
    public void AConfiguredFileReadsBackAndNamesItsHosts()
    {
        using var signer = new TestSigner("2026-1");
        var trust = UpdateTrust.Parse(File(signer: signer));

        Assert.True(trust.Configured);
        Assert.Equal(new Uri("https://updates.screentail.example/feed/"), trust.Feed);
        Assert.Equal(["downloads.screentail.example", "updates.screentail.example"], trust.Hosts.Order(StringComparer.Ordinal));
        Assert.Equal("2026-1", Assert.Single(trust.Keys).Id);
        Assert.Equal(new Uri("https://updates.screentail.example/feed/pilot/win-x64.json"), trust.ManifestFor(UpdateChannel.Pilot, "x64"));
        Assert.Equal(new Uri("https://updates.screentail.example/feed/stable/win-x64.json"), trust.ManifestFor(UpdateChannel.Stable, "x64"));
    }

    [Theory]
    [InlineData("http://updates.screentail.example/feed/")]
    [InlineData("https://updates.screentail.example/feed")]
    [InlineData("https://updates.screentail.example:8443/feed/")]
    [InlineData("https://updates.screentail.example/feed/?device=1")]
    [InlineData("https://someone@updates.screentail.example/feed/")]
    [InlineData("feed/")]
    public void AFeedThatIsNotAPlainHttpsFolderIsRefused(string feed)
    {
        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(feed: feed)));
    }

    [Theory]
    [InlineData("*.screentail.example")]
    [InlineData("downloads.screentail.example/path")]
    [InlineData("")]
    public void APackageHostMustBeOneExactName(string host)
    {
        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(host: host)));
    }

    [Fact]
    public void AKeyThatIsNotP256IsRefused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(spki: Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()))));
        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(spki: Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()))));
    }

    private static string File(string? feed = "https://updates.screentail.example/feed/", string host = "downloads.screentail.example", string? spki = null, TestSigner? signer = null)
    {
        using var made = signer is null ? new TestSigner() : null;
        var key = (signer ?? made)!.Public;
        return new JsonObject
        {
            ["feed"] = feed,
            ["packageHosts"] = new JsonArray(host),
            ["keys"] = new JsonArray(new JsonObject { ["id"] = key.Id, ["spki"] = spki ?? Convert.ToBase64String(key.SubjectPublicKeyInfo) }),
        }.ToJsonString();
    }
}
