using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ScreenTrail.Core.Net;
using ScreenTrail.Core.Update;

namespace ScreenTrail.Tests.Update;

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
        Assert.Empty(ClientEgress.Settings(new Uri("https://api.screentrail.example/")).UpdateHosts);
    }

    [Fact]
    public void AConfiguredFileReadsBackAndNamesItsHosts()
    {
        using var signer = new TestSigner("2026-1");
        var trust = UpdateTrust.Parse(File(signer: signer));

        Assert.True(trust.Configured);
        Assert.Equal(new Uri("https://updates.screentrail.example/feed/"), trust.Feed);
        Assert.Equal(["downloads.screentrail.example", "updates.screentrail.example"], trust.Hosts.Order(StringComparer.Ordinal));
        Assert.Equal("2026-1", Assert.Single(trust.Keys).Id);
        Assert.Equal(new Uri("https://updates.screentrail.example/feed/pilot/win-x64.json"), trust.ManifestFor(UpdateChannel.Pilot, "x64"));
        Assert.Equal(new Uri("https://updates.screentrail.example/feed/stable/win-x64.json"), trust.ManifestFor(UpdateChannel.Stable, "x64"));
    }

    [Theory]
    [InlineData("http://updates.screentrail.example/feed/")]
    [InlineData("https://updates.screentrail.example/feed")]
    [InlineData("https://updates.screentrail.example:8443/feed/")]
    [InlineData("https://updates.screentrail.example/feed/?device=1")]
    [InlineData("https://someone@updates.screentrail.example/feed/")]
    [InlineData("feed/")]
    public void AFeedThatIsNotAPlainHttpsFolderIsRefused(string feed)
    {
        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(feed: feed)));
    }

    [Theory]
    [InlineData("*.screentrail.example")]
    [InlineData("downloads.screentrail.example/path")]
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

    [Fact]
    public void EachKeyIsReadWithTheOneChannelItIsTrustedFor()
    {
        using var pilot = new TestSigner("pilot-1", UpdateChannel.Pilot);
        using var stable = new TestSigner("stable-1", UpdateChannel.Stable);

        var trust = UpdateTrust.Parse(File(keys: [KeyEntry(pilot), KeyEntry(stable)]));

        Assert.Equal(UpdateChannel.Pilot, trust.Keys.Single(k => k.Id == "pilot-1").Channel);
        Assert.Equal(UpdateChannel.Stable, trust.Keys.Single(k => k.Id == "stable-1").Channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("Stable")]
    [InlineData("pilot,stable")]
    public void AKeyWithoutExactlyOneChannelIsRefused(string? channel)
    {
        // The trust file says which channel each key may sign for, and nothing else is guessed at: a key
        // with no channel, or one that names both, would let one key sign both channels.
        using var signer = new TestSigner();
        var entry = KeyEntry(signer);
        if (channel is null)
        {
            _ = entry.Remove("channel");
        }
        else
        {
            entry["channel"] = channel;
        }

        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(keys: [entry])));
    }

    [Fact]
    public void AKeyChannelThatIsNotAStringIsRefused()
    {
        using var signer = new TestSigner();
        var entry = KeyEntry(signer);
        entry["channel"] = new JsonArray("pilot", "stable");

        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(keys: [entry])));
    }

    [Fact]
    public void OneKeyListedForBothChannelsIsRefused()
    {
        // The same public key under two ids, one per channel, is one key trusted for both.
        using var signer = new TestSigner();
        var asPilot = KeyEntry(signer);
        asPilot["id"] = "as-pilot";
        asPilot["channel"] = "pilot";
        var asStable = KeyEntry(signer);
        asStable["id"] = "as-stable";
        asStable["channel"] = "stable";

        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(keys: [asPilot, asStable])));
    }

    [Fact]
    public void OneIdForTwoKeysIsRefused()
    {
        using var pilot = new TestSigner("same-id", UpdateChannel.Pilot);
        using var stable = new TestSigner("same-id", UpdateChannel.Stable);

        Assert.Throws<FormatException>(() => UpdateTrust.Parse(File(keys: [KeyEntry(pilot), KeyEntry(stable)])));
    }

    [Fact]
    public void TheRepositoryTrustFileHasTheChannelShapeEvenWhileEmpty()
    {
        // shared/release/update-trust.json, as committed: it parses with the strict reader (or the build
        // would check for nothing without saying why), and it documents that each key names its channel.
        var path = Path.Combine(RepositoryRoot(), "shared", "release", "update-trust.json");
        var text = System.IO.File.ReadAllText(path);

        var trust = UpdateTrust.Parse(text);
        Assert.False(trust.Configured);
        Assert.Contains("channel", JsonNode.Parse(text)!["_comment"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.All(JsonNode.Parse(text)!["keys"]!.AsArray(), k => Assert.NotNull(k!["channel"]));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "shared", "release", "update-trust.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("shared/release/update-trust.json was not found above the test binary.");
    }

    private static JsonObject KeyEntry(TestSigner signer)
    {
        var key = signer.Public;
        return new JsonObject { ["id"] = key.Id, ["channel"] = key.Channel.ToWire(), ["spki"] = Convert.ToBase64String(key.SubjectPublicKeyInfo) };
    }

    private static string File(string? feed = "https://updates.screentrail.example/feed/", string host = "downloads.screentrail.example", string? spki = null, TestSigner? signer = null, JsonObject[]? keys = null)
    {
        using var made = signer is null ? new TestSigner() : null;
        var entry = KeyEntry((signer ?? made)!);
        if (spki is not null)
        {
            entry["spki"] = spki;
        }

        var list = new JsonArray();
        foreach (var key in keys ?? [entry])
        {
            list.Add(key);
        }

        return new JsonObject
        {
            ["feed"] = feed,
            ["packageHosts"] = new JsonArray(host),
            ["keys"] = list,
        }.ToJsonString();
    }
}
