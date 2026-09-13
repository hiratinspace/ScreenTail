using System.Net;
using System.Security.Cryptography;
using System.Text;
using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// ST-027's "model absent → progress shown, app responsive", and the resume the ticket asks for.
///
/// The model is hundreds of megabytes and arrives while a technician is on a call, so the failure modes
/// are the point: a laptop that slept mid-download, a server that ignores a Range request, a file that
/// arrives truncated. Each is played against a stub that behaves the way a real one misbehaves.
/// </summary>
public sealed class ModelDownloadTests : IDisposable
{
    private static readonly byte[] Weights = Encoding.UTF8.GetBytes(new string('w', 50_000));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    private static SpeechModel Model(long? bytes = null) => new(
        "Small",
        new Uri("https://models.example/ggml-small.bin"),
        bytes ?? Weights.Length,
        Convert.ToHexStringLower(SHA256.HashData(Weights)));

    [Fact]
    public async Task AMissingModelIsFetchedAndVerified()
    {
        var server = new StubServer(Weights);
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");

        Assert.True(await download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.Equal(Weights, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".partial"), "the partial file was left behind");
    }

    [Fact]
    public async Task AModelAlreadyOnDiskIsNotDownloadedAgain()
    {
        // Hundreds of megabytes on every service start would be its own bug.
        var server = new StubServer(Weights);
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(path, Weights, TestContext.Current.CancellationToken);

        Assert.True(await download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task AFileOnDiskThatIsNotTheModelIsReplaced()
    {
        // A truncated model from a previous version, or a corrupted one. It has the right name and the
        // wrong contents, and loading it is worse than not having it.
        var server = new StubServer(Weights);
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(path, "not a model", TestContext.Current.CancellationToken);

        Assert.True(await download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.Equal(Weights, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesWhereItStopped()
    {
        // The laptop slept. Starting 500 MB again is the difference between this working on a real machine
        // and not, and the criterion says "lazy resumable".
        var server = new StubServer(Weights);
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(path + ".partial", Weights[..20_000], TestContext.Current.CancellationToken);

        Assert.True(await download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.Equal(Weights, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(20_000, server.ResumedFrom);
        Assert.Equal(Weights.Length - 20_000, server.BytesServed);
    }

    [Fact]
    public async Task AServerThatIgnoresTheRangeStillProducesTheRightFile()
    {
        // Plenty of CDNs answer 200 with the whole file. Appending that to what is already on disk would
        // give a file with the first bytes twice — which fails the hash only after the whole download.
        var server = new StubServer(Weights) { HonourRange = false };
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(path + ".partial", Weights[..20_000], TestContext.Current.CancellationToken);

        Assert.True(await download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.Equal(Weights, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APartialLongerThanTheModelIsStartedOver()
    {
        // The model changed, or the file is something else entirely. It cannot be a prefix of what we
        // want, so resuming from it would append to rubbish and fail the hash after another 500 MB.
        var server = new StubServer(Weights);
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(path + ".partial", new byte[Weights.Length + 5_000], TestContext.Current.CancellationToken);

        Assert.True(await download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, server.ResumedFrom);
        Assert.Equal(Weights, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AModelThatFailsItsHashIsNeverPutInPlace()
    {
        // The claim that matters. A model is executable input to a process that reads a technician's
        // screen: a wrong file is not a broken feature, it is untrusted weights.
        var server = new StubServer(Encoding.UTF8.GetBytes("something else entirely"));
        var download = new ModelDownload(new HttpClient(server));
        var path = Path.Combine(_dir, "model.bin");

        await Assert.ThrowsAsync<ModelIntegrityException>(
            () => download.EnsureAsync(Model(), path, ct: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path), "a model that failed its hash was put in place");
        Assert.False(File.Exists(path + ".partial"), "a file known to be wrong was kept to resume from");
    }

    [Fact]
    public async Task ProgressIsReportedFromStartToFinish()
    {
        // "Model absent → progress shown, app responsive". Without this, downloading and hanging look the
        // same from outside, and the technician restarts the service in the middle of a support call.
        var server = new StubServer(Weights);
        var download = new ModelDownload(new HttpClient(server)) { ReportEvery = TimeSpan.Zero };
        var path = Path.Combine(_dir, "model.bin");

        // Collected synchronously. Progress<T> posts to the captured synchronization context, so the
        // reports arrive after the download returns and the assertions become a race against the thread
        // pool — the test would pass locally and fail on a loaded CI machine.
        var seen = new Collected();

        await download.EnsureAsync(Model(), path, seen, TestContext.Current.CancellationToken);

        Assert.NotEmpty(seen.Reports);
        Assert.Equal(1.0, seen.Reports[^1].Fraction);
        Assert.All(seen.Reports, p => Assert.InRange(p.Fraction, 0, 1));
        Assert.True(
            seen.Reports.Any(p => p.Fraction > 0 && p.Fraction < 1) || seen.Reports.Count >= 2,
            "progress went straight from nothing to done, which tells the technician nothing");
    }

    /// <summary>Records reports on the thread that raised them, so a test can assert without waiting.</summary>
    private sealed class Collected : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public void AFractionOfAnUnknownSizeIsZeroRatherThanInfinite()
    {
        Assert.Equal(0, new DownloadProgress(1_000, 0).Fraction);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Serves the bytes, and misbehaves the way real servers do when asked.</summary>
    private sealed class StubServer(byte[] content) : HttpMessageHandler
    {
        public bool HonourRange { get; init; } = true;

        public int Requests { get; private set; }

        public long ResumedFrom { get; private set; }

        public long BytesServed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            ResumedFrom = from;

            if (from > 0 && HonourRange)
            {
                var slice = content[(int)from..];
                BytesServed = slice.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                });
            }

            BytesServed = content.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }
}
