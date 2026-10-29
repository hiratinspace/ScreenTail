using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScreenTail.Api.Tests.Providers.StandIns;

/// <summary>One file part of a multipart request, as the stand-in received it. The bytes are counted, not kept.</summary>
internal sealed record RecordedFile(string Field, string FileName, string ContentType, long Length);

/// <summary>
/// One request exactly as it arrived over the wire: the method, the path and query, every header, and
/// the body (as text for JSON, as fields and file parts for a form). What a provider test asserts
/// against, so it says what the provider sent rather than what it meant to send.
/// </summary>
internal sealed class RecordedRequest
{
    public required DateTimeOffset At { get; init; }

    public required string Method { get; init; }

    /// <summary>The path below the stand-in's root, decoded, starting with a slash.</summary>
    public required string Path { get; init; }

    /// <summary>The raw query string, with its leading question mark, as it was on the wire.</summary>
    public required string RawQuery { get; init; }

    public required IReadOnlyDictionary<string, string> Query { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The body as text, for anything that is not a form. Null when there was none.</summary>
    public string? Body { get; init; }

    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<RecordedFile> Files { get; init; } = [];

    /// <summary>What the stand-in answered.</summary>
    public int Status { get; set; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public JsonElement Json() => JsonDocument.Parse(Body ?? throw new InvalidOperationException($"{Method} {Path} had no body.")).RootElement.Clone();
}

/// <summary>What the stand-in sends back: a status, an optional JSON body, optional headers.</summary>
internal sealed record Reply(int Status, object? Body = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// A provider's HTTP API, stood in on this machine for the provider tests: a real Kestrel server on a
/// loopback port, over HTTPS with a certificate made for the run, so the real provider code goes through
/// the real handler Program.cs builds (no redirects, no proxy, the connect-time address check with the
/// test switch on) and over a real socket. Every request is recorded as it arrived, and a test can ask
/// for the next matching request to be answered with a failure instead of the route.
///
/// The address rule is not weakened to reach it: the tests turn on
/// <c>ProviderNetwork:AllowPrivateAddresses</c>, the switch that exists for tests and Development and
/// that the service refuses outside Development. The certificate is trusted by the provider's handler
/// only, by its exact thumbprint, and only in the test host (<see cref="TrustThisCertificateOnly"/>).
/// </summary>
internal abstract class StandInHost : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> Certificate = new(MakeCertificate);

    private readonly Lock _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<(Func<RecordedRequest, bool> When, Reply Reply, int Left)> _injected = [];
    private WebApplication? _app;

    /// <summary>Where the stand-in answers: <c>https://127.0.0.1:port/</c>. What a tenant stores as its site.</summary>
    public Uri Site { get; private set; } = new("https://127.0.0.1/");

    /// <summary>Every request so far, in arrival order. A copy, taken under the lock: the publisher sends destinations and screenshots at once.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// The provider's named client, made to trust this run's certificate and nothing else that is not
    /// trusted already. Checks first that the handler is the one Program.cs builds, so a test cannot pass
    /// through a handler with redirects or a proxy that the service would never use.
    /// </summary>
    public static void TrustThisCertificateOnly(HttpMessageHandler primary)
    {
        if (primary is not SocketsHttpHandler { AllowAutoRedirect: false, UseProxy: false, UseCookies: false, ConnectCallback: not null } sockets)
        {
            throw new InvalidOperationException("The provider client's handler is not the PublicNetwork handler Program.cs builds.");
        }

        var expected = Certificate.Value.GetCertHashString(HashAlgorithmName.SHA256);
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, errors) =>
            errors is SslPolicyErrors.None
            || (presented is not null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                && string.Equals(presented.GetCertHashString(HashAlgorithmName.SHA256), expected, StringComparison.Ordinal));
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // The stand-in writes no log of its own: the only log a test reads is the backend's.
        _ = builder.Logging.ClearProviders();
        _ = builder.WebHost.UseKestrelHttpsConfiguration();
        _ = builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate.Value)));
        _app = builder.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync(TestContext.Current.CancellationToken);

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Site = new Uri(address.TrimEnd('/') + "/");
    }

    /// <summary>The next <paramref name="times"/> requests that match are answered with this instead of the route. Checked after authentication, as a real API would.</summary>
    public void Inject(Func<RecordedRequest, bool> when, Reply reply, int times = 1)
    {
        lock (_gate)
        {
            _injected.Add((when, reply, times));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The refusal for a request whose credentials are wrong or missing, or null to carry on.</summary>
    protected abstract Reply? Authenticate(RecordedRequest request);

    /// <summary>The route's answer. Called under the stand-in's lock, so state changes need no more.</summary>
    protected abstract Reply Answer(RecordedRequest request);

    /// <summary>Runs <paramref name="change"/> under the same lock the routes run under.</summary>
    protected T Locked<T>(Func<T> change)
    {
        lock (_gate)
        {
            return change();
        }
    }

    protected static string RandomToken(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    private async Task HandleAsync(HttpContext context)
    {
        var recorded = await RecordAsync(context.Request, context.RequestAborted);
        Reply reply;
        lock (_gate)
        {
            _requests.Add(recorded);
            reply = Authenticate(recorded) ?? TakeInjected(recorded) ?? Answer(recorded);
            recorded.Status = reply.Status;
        }

        context.Response.StatusCode = reply.Status;
        foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[name] = value;
        }

        if (reply.Body is not null)
        {
            await context.Response.WriteAsJsonAsync(reply.Body, reply.Body.GetType(), JsonSerializerOptions.Web, context.RequestAborted);
        }
    }

    private Reply? TakeInjected(RecordedRequest request)
    {
        for (var i = 0; i < _injected.Count; i++)
        {
            var (when, reply, left) = _injected[i];
            if (!when(request))
            {
                continue;
            }

            if (left <= 1)
            {
                _injected.RemoveAt(i);
            }
            else
            {
                _injected[i] = (when, reply, left - 1);
            }

            return reply;
        }

        return null;
    }

    private static async Task<RecordedRequest> RecordAsync(HttpRequest request, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = values.ToString();
        }

        var query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        string? body = null;
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<RecordedFile>();
        if (request.HasFormContentType)
        {
            var read = await request.ReadFormAsync(ct);
            foreach (var (name, value) in read)
            {
                form[name] = value.ToString();
            }

            files.AddRange(read.Files.Select(f => new RecordedFile(f.Name, f.FileName, f.ContentType, f.Length)));
        }
        else if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync(ct);
        }

        return new RecordedRequest
        {
            At = DateTimeOffset.UtcNow,
            Method = request.Method,
            Path = request.Path.Value ?? "/",
            RawQuery = request.QueryString.Value ?? string.Empty,
            Query = query,
            Headers = headers,
            Body = body,
            Form = form,
            Files = files,
        };
    }

    /// <summary>
    /// A certificate for 127.0.0.1, made once per run and never written anywhere. Self-signed, so no
    /// machine trusts it; the provider client in the test host trusts it by thumbprint.
    /// </summary>
    private static X509Certificate2 MakeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ScreenTail provider stand-in", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        // Exported and loaded again so the private key is usable by the TLS stack on every platform;
        // the password exists only for this round trip.
        var password = RandomToken(16);
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12, password), password);
    }
}
