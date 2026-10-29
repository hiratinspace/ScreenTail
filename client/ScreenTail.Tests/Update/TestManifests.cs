using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ScreenTail.Core.Update;

namespace ScreenTail.Tests.Update;

/// <summary>
/// A signing key made for one test run, never stored anywhere: the stand-in for the release key in Azure
/// Key Vault, with the same algorithm (ECDSA P-256, SHA-256, r then s).
/// </summary>
internal sealed class TestSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestSigner(string id = "test-key") => Id = id;

    public string Id { get; }

    public UpdateKey Public => new(Id, _key.ExportSubjectPublicKeyInfo());

    public byte[] Sign(byte[] message) =>
        _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>The same signature in its other common encoding: an ASN.1 SEQUENCE of r and s.</summary>
    public byte[] SignDer(byte[] message) =>
        _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose() => _key.Dispose();
}

/// <summary>Manifests and packages built the way CI's signing job builds them, for the Mac suite.</summary>
internal static class TestManifests
{
    public static readonly Uri Feed = new("https://updates.screentail.example/feed/");

    public const string PackageHost = "downloads.screentail.example";

    public static readonly Version Installed = new(0, 1, 10);

    /// <summary>The bytes the test package is made of: not an MSI, which only Windows reads.</summary>
    public static readonly byte[] Package = Encoding.ASCII.GetBytes("not really an msi, but exactly these bytes " + new string('x', 4000));

    public static string PackageSha256 => Convert.ToHexStringLower(SHA256.HashData(Package));

    public static Uri PackageUrl(string version = "0.1.20") => new($"https://{PackageHost}/screentail/{version}/ScreenTail.msi");

    public static UpdateTrust Trust(params UpdateKey[] keys) =>
        new(Feed, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PackageHost }, keys);

    /// <summary>The signed part, as CI writes it. Change one field with <paramref name="edit"/>.</summary>
    public static JsonObject Payload(string version = "0.1.20", Action<JsonObject>? edit = null)
    {
        var payload = new JsonObject
        {
            ["format"] = UpdateManifestReader.PayloadFormat,
            ["channel"] = "stable",
            ["architecture"] = "x64",
            ["version"] = version,
            ["minimumVersion"] = "0.1.0",
            ["package"] = new JsonObject
            {
                ["url"] = PackageUrl(version).AbsoluteUri,
                ["sha256"] = PackageSha256,
                ["size"] = Package.Length,
            },
            ["published"] = "2026-10-28T12:00:00Z",
        };
        edit?.Invoke(payload);
        return payload;
    }

    /// <summary>The envelope: format, key id, the signed bytes and the signature over exactly them.</summary>
    public static byte[] Envelope(TestSigner signer, JsonObject payload, string? keyId = null, Func<byte[], byte[]>? sign = null, string format = UpdateManifestReader.EnvelopeFormat)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        return Envelope(bytes, (sign ?? signer.Sign)(bytes), keyId ?? signer.Id, format);
    }

    public static byte[] Envelope(byte[] signedBytes, byte[] signature, string keyId, string format = UpdateManifestReader.EnvelopeFormat) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = format,
            ["key"] = keyId,
            ["payload"] = Convert.ToBase64String(signedBytes),
            ["signature"] = Convert.ToBase64String(signature),
        }.ToJsonString());

    public static ManifestVerdict Read(byte[] envelope, UpdateKey key, UpdateChannel channel = UpdateChannel.Stable, Version? installed = null, Version? newestSeen = null) =>
        UpdateManifestReader.Read(envelope, Trust(key), channel, "x64", installed ?? Installed, newestSeen);
}

/// <summary>
/// The far side of the egress guard: the feed and the package host, answering what each test scripts and
/// recording every request that reached it, headers, purpose and all.
/// </summary>
internal sealed class FakeFeed : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);

    /// <summary>What reached the far side, copied as it arrived (the sender disposes its request).</summary>
    public List<Seen> Requests { get; } = [];

    public List<string> Urls => [.. Requests.Select(r => r.Url)];

    public FakeFeed Answer(Uri url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(url);
        _answers[url.AbsoluteUri] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _ = request.Options.TryGetValue(Core.Net.EgressGuard.PurposeKey, out var purpose);
        Requests.Add(new Seen(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            [.. request.Headers.Select(h => h.Key)],
            request.Content is not null,
            purpose));
        return Task.FromResult(_answers.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
            ? answer()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    internal sealed record Seen(HttpMethod Method, string Url, IReadOnlyList<string> Headers, bool HasBody, Core.Net.EgressPurpose Purpose);
}
