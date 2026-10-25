using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using ScreenTail.Core.Net;
using ScreenTail.Core.Speech;
using ScreenTail.Service.Speech;

namespace ScreenTail.Tests.Windows.Speech;

/// <summary>
/// How the recogniser fails when the model cannot be had (ST-027, 2026-10-12 review T-92).
///
/// Every path here is one that has happened or will: the model host off the allowlist, the publisher's
/// server down, a file that is not the model. Each used to be, or could be, an exception out of a task
/// nobody awaited, which is a service with no narration and no line saying why. Here rather than in the
/// Mac suite only because the recogniser is a Windows type; nothing in it needs a sound card, a model
/// file or the network, so it runs on the hosted runner and never skips.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WhisperRecogniserTests : IDisposable
{
    private static readonly byte[] NotAModel = Encoding.UTF8.GetBytes(new string('x', 4_096));

    private static readonly SpeechModel Elsewhere = new(
        "tiny.en",
        new Uri("https://models.example/ggml-tiny.en.bin"),
        NotAModel.Length,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(NotAModel)));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AModelHostTheGuardRefusesIsReportedNotThrown()
    {
        // The failure that actually happened in 2026-09-20: the publisher redirected to a CDN that was
        // not on the allowlist. The default policy allows no model host at all, so the first hop is
        // refused here, and the answer is false with the host named, never an exception.
        var ct = TestContext.Current.CancellationToken;
        await using var recogniser = new WhisperRecogniser(
            new ModelDownload(new HttpClient(new EgressGuard(new EgressPolicy()))),
            Elsewhere,
            _dir);

        Assert.False(await recogniser.PrepareAsync(ct));

        Assert.False(recogniser.Ready);
        Assert.False(recogniser.Loaded);
        Assert.NotNull(recogniser.Unavailable);
        Assert.Contains("models.example", recogniser.Unavailable, StringComparison.Ordinal);
        Assert.DoesNotContain("ggml-tiny", recogniser.Unavailable, StringComparison.Ordinal);
        Assert.Null(await recogniser.TranscribeAsync(new SpeechSegment(0, 1_000, Truncated: false), new short[16_000], ct));
    }

    [Fact]
    public async Task AServerThatWillNotServeTheModelIsReportedNotThrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new StubServer(HttpStatusCode.ServiceUnavailable, []);
        await using var recogniser = new WhisperRecogniser(new ModelDownload(new HttpClient(server)), Elsewhere, _dir);

        Assert.False(await recogniser.PrepareAsync(ct));

        Assert.False(recogniser.Ready);
        Assert.Equal(nameof(HttpRequestException), recogniser.Unavailable);
    }

    [Fact]
    public async Task AFileThatIsNotTheModelIsReportedNotThrownAndNotKept()
    {
        // The right length and the wrong bytes: a proxy's error page, a truncated mirror. The hash
        // refuses it, the file is deleted rather than loaded, and the reason is a type name, never the
        // bytes (INV-10).
        var ct = TestContext.Current.CancellationToken;
        var server = new StubServer(HttpStatusCode.OK, Encoding.UTF8.GetBytes(new string('y', NotAModel.Length)));
        await using var recogniser = new WhisperRecogniser(new ModelDownload(new HttpClient(server)), Elsewhere, _dir);

        Assert.False(await recogniser.PrepareAsync(ct));

        Assert.Equal(nameof(ModelIntegrityException), recogniser.Unavailable);
        Assert.False(File.Exists(Path.Combine(_dir, "ggml-tiny.en.bin")), "a file that failed its hash was left where the loader would find it");
    }

    [Fact]
    public async Task AFileThatMatchesButWillNotLoadIsReportedNotThrown()
    {
        // The hash matches, so the download is kept, and whisper.cpp still cannot read it as a model.
        // The loader's refusal is reported the same way as a download's.
        var ct = TestContext.Current.CancellationToken;
        var server = new StubServer(HttpStatusCode.OK, NotAModel);
        await using var recogniser = new WhisperRecogniser(new ModelDownload(new HttpClient(server)), Elsewhere, _dir);

        Assert.False(await recogniser.PrepareAsync(ct));

        Assert.False(recogniser.Ready);
        Assert.False(recogniser.Loaded);
        Assert.NotNull(recogniser.Unavailable);
    }

    [Fact]
    public async Task AFailedPreparationIsTriedAgainNextTime()
    {
        // The host that refused this morning may allow it this afternoon, once somebody has fixed the
        // allowlist. A failure is not remembered as "no model, ever".
        var ct = TestContext.Current.CancellationToken;
        var server = new StubServer(HttpStatusCode.ServiceUnavailable, []);
        await using var recogniser = new WhisperRecogniser(new ModelDownload(new HttpClient(server)), Elsewhere, _dir);

        Assert.False(await recogniser.PrepareAsync(ct));
        Assert.False(await recogniser.PrepareAsync(ct));

        Assert.Equal(2, server.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class StubServer(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }
}
