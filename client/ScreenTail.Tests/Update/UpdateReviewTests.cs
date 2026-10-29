using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Core.Net;
using ScreenTail.Core.Update;
using static ScreenTail.Tests.Update.TestManifests;

namespace ScreenTail.Tests.Update;

/// <summary>
/// The 2026-10-29 adversarial review of ST-113: the folder prepared before anything is written in it,
/// the download's size cap, a download that stops when a session starts and resumes after, an install
/// check that a concurrent check cannot race, cookies, a channel that is behind, and stale packages.
/// </summary>
public sealed class UpdateReviewTests : IDisposable
{
    private static readonly Uri StableManifest = new(Feed, "stable/win-x64.json");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private readonly TestSigner _signer = new();
    private readonly ListLogger _log = new();
    private bool _busy;

    [Fact]
    public async Task TheFolderIsPreparedBeforeAnythingIsWrittenInIt()
    {
        // The Windows folder sets its permissions when it creates the folder. A check that wrote its
        // record of versions seen first made the folder with the profile's inherited permissions, and the
        // protected ones were never applied.
        var folder = new RecordingFolder(_dir);
        var feed = new FakeFeed().Answer(StableManifest, Envelope(_signer, Payload("0.1.10")));

        var status = await Service(feed, folder).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.True(folder.Prepared > 0, "the folder was prepared");
        Assert.False(folder.ExistedWhenFirstPrepared, "nothing created the folder before Prepare did");
    }

    [Fact]
    public async Task AStreamedPackageLongerThanItsManifestSaysIsCutOffNearTheStatedSize()
    {
        // No Content-Length to refuse on: the cap while streaming is the only thing that stops it.
        var endless = new ChunkedStream([.. Package, .. new byte[8 * 1024 * 1024]]);
        var feed = new StreamingFeed(Envelope(_signer, Payload()), _ => endless);

        var status = await Service(feed).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Refused, status.State);
        Assert.InRange(endless.Delivered, Package.Length, Package.Length + (2 * 81920) + ChunkedStream.Chunk);
        Assert.Empty(Directory.GetFiles(_dir, "*.msi*"));
    }

    [Fact]
    public async Task ADownloadStopsWhenASessionStartsAndResumesWhereItStopped()
    {
        var big = RandomNumberGenerator.GetBytes(1024 * 1024);
        var payload = Payload(edit: p =>
        {
            p["package"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(big));
            p["package"]!["size"] = big.Length;
        });
        ChunkedStream? first = null;
        var feed = new StreamingFeed(Envelope(_signer, payload), range =>
        {
            if (range is { } from)
            {
                return new ChunkedStream(big[(int)from..]);
            }

            first = new ChunkedStream(big, afterFirstChunk: () => _busy = true);
            return first;
        }, size: big.Length);
        var updates = Service(feed);

        var stopped = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, stopped.State);
        Assert.Null(updates.Ready);
        Assert.True(first!.Delivered < big.Length, "the download stopped before the end");
        var partial = Assert.Single(Directory.GetFiles(_dir, "*.partial"));
        var kept = new FileInfo(partial).Length;
        Assert.InRange(kept, 1, big.Length - 1);

        _busy = false;
        var resumed = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Ready, resumed.State);
        Assert.Equal(kept, feed.RangesAsked.Single());
        Assert.Equal(big, await File.ReadAllBytesAsync(updates.Ready!.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFeedThatIgnoresTheRangeStartsTheDownloadAgainRatherThanAppending()
    {
        var big = RandomNumberGenerator.GetBytes(256 * 1024);
        var payload = Payload(edit: p =>
        {
            p["package"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(big));
            p["package"]!["size"] = big.Length;
        });
        var interrupt = true;
        var feed = new StreamingFeed(Envelope(_signer, payload), _ => new ChunkedStream(big, afterFirstChunk: () => _busy = interrupt), honourRange: false);
        var updates = Service(feed);
        _ = await updates.CheckAsync(TestContext.Current.CancellationToken);

        _busy = false;
        interrupt = false;
        var again = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Ready, again.State);
        Assert.Equal(big, await File.ReadAllBytesAsync(updates.Ready!.Path, TestContext.Current.CancellationToken));
    }

    public static TheoryData<string> BadResumes() => ["416", "206 at the wrong offset", "206 of another total", "206 with no range"];

    [Theory]
    [MemberData(nameof(BadResumes))]
    public async Task AResumeAnsweredWronglyThrowsTheKeptBytesAwaySoTheNextCheckStartsFromZero(string answer)
    {
        // Second review: anything but a 200 or a 206 whose Content-Range starts at the bytes kept and
        // totals the manifest's size is a feed that cannot be trusted to continue this file. The kept
        // bytes go, nothing of the answer is written, and the next check starts again from the beginning.
        var (updates, feed, big, kept) = await InterruptedAsync();
        feed.Resume = from => answer switch
        {
            "416" => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable),
            "206 at the wrong offset" => Partial(big, from - 10, big.Length),
            "206 of another total" => Partial(big, from, big.Length + 1),
            _ => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(big[(int)from..]) },
        };

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
        Assert.DoesNotContain(_log.Lines, l => l.Level >= LogLevel.Warning);

        feed.Resume = null;
        var again = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Ready, again.State);
        Assert.Equal([kept], feed.RangesAsked);
        Assert.Equal(big, await File.ReadAllBytesAsync(updates.Ready!.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallDoesNotWaitBehindARunningCheck()
    {
        // Second review: Install is answered on the pipe's worker. Waiting there for a check that is
        // downloading 100 MB would hold every other question behind it, so it answers at once instead.
        var feed = new FakeFeed().Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), Package);
        var updates = Service(feed);
        _ = await updates.CheckAsync(TestContext.Current.CancellationToken);
        using var publisher = new BlockingPublisher();
        publisher.Release(Identity("CN=Ours"));
        var installer = new UpdateInstaller(updates, publisher, new NeverLauncher(), Identity("CN=Ours"), _ => Task.FromResult<IDisposable?>(new NoHold()));
        using var running = await updates.ExclusiveAsync(TestContext.Current.CancellationToken);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal(UpdateInstaller.CheckRunning, answer.Refusal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"answered in {watch.Elapsed}");
        Assert.False(installer.Installing);
    }

    [Fact]
    public void StalePackagesAreLeftAloneInAFolderThatIsALink()
    {
        // Second review: the folder is prepared (and so refused when it is a link) before anything in it
        // is deleted, so a link planted where the folder belongs cannot point the clean-up elsewhere.
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "ScreenTail-0.1.5.msi"), "x");
        var link = Path.Combine(_dir, "Updates");
        Directory.CreateSymbolicLink(link, elsewhere);

        Service(new FakeFeed(), new PlainUpdateFolder(link)).RemoveStalePackages();

        Assert.True(File.Exists(Path.Combine(elsewhere, "ScreenTail-0.1.5.msi")));
    }

    private static HttpResponseMessage Partial(byte[] package, long from, long total)
    {
        var content = new ByteArrayContent(package[(int)Math.Max(from, 0)..]);
        content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, package.Length - 1, total);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    /// <summary>A download a session stopped part of the way through, with what it kept on disk.</summary>
    private async Task<(UpdateService Updates, StreamingFeed Feed, byte[] Package, long Kept)> InterruptedAsync()
    {
        var big = RandomNumberGenerator.GetBytes(512 * 1024);
        var payload = Payload(edit: p =>
        {
            p["package"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(big));
            p["package"]!["size"] = big.Length;
        });
        var first = true;
        var feed = new StreamingFeed(Envelope(_signer, payload), range =>
        {
            if (range is { } from)
            {
                return new ChunkedStream(big[(int)from..]);
            }

            var interrupt = first;
            first = false;
            return new ChunkedStream(big, afterFirstChunk: () => _busy = interrupt);
        }, size: big.Length);
        var updates = Service(feed);
        Assert.Equal(UpdateState.Available, (await updates.CheckAsync(TestContext.Current.CancellationToken)).State);
        _busy = false;
        var kept = new FileInfo(Assert.Single(Directory.GetFiles(_dir, "*.partial"))).Length;
        return (updates, feed, big, kept);
    }

    [Fact]
    public async Task AnInstallsCheckAndAConcurrentCheckDoNotInterleave()
    {
        // While Install is checking the package, a check that found a new release could otherwise replace
        // the ready package, and Install's refusal would delete the wrong one.
        var feed = new FakeFeed().Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), Package);
        var updates = Service(feed);
        Assert.Equal(UpdateState.Ready, (await updates.CheckAsync(TestContext.Current.CancellationToken)).State);
        using var publisher = new BlockingPublisher();
        var installer = new UpdateInstaller(updates, publisher, new NeverLauncher(), Identity("CN=Ours"), _ => Task.FromResult<IDisposable?>(new NoHold()));

        var installing = Task.Run(() => installer.InstallAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.True(publisher.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var checking = updates.CheckAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(checking.IsCompleted, "the check waited for the install's check to finish");

        publisher.Release(Identity("CN=Somebody Else"));
        var answer = await installing;
        _ = await checking;

        Assert.False(answer.Ok);
    }

    [Fact]
    public async Task AReleasedHoldAsksForTheForegroundToBeLookedAtAgain()
    {
        // A tool that took focus while Windows Installer ran was refused its session. When the hold goes
        // (the technician cancelled at the prompt), the window in front is decided again.
        var feed = new FakeFeed().Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), Package);
        var updates = Service(feed);
        _ = await updates.CheckAsync(TestContext.Current.CancellationToken);
        var released = 0;
        var launcher = new ExitingLauncher(1602);
        using var publisher = new BlockingPublisher();
        publisher.Release(Identity("CN=Ours"));
        var installer = new UpdateInstaller(updates, publisher, launcher, Identity("CN=Ours"), _ => Task.FromResult<IDisposable?>(new NoHold()), () => released++);

        Assert.True((await installer.InstallAsync(TestContext.Current.CancellationToken)).Ok);
        await installer.Finished;

        Assert.Equal(1, released);
    }

    [Fact]
    public void TheUpdateClientKeepsNoCookiesAndFollowsNoRedirectBehindTheGuard()
    {
        // A cookie the feed sets would otherwise go back to it on the next check: a device identifier in
        // all but name. Redirects stay the guard's, so every hop is decided by the same policy.
        using var guard = EgressGuard.ForUpdates(new EgressPolicy());

        var inner = Assert.IsType<HttpClientHandler>(guard.InnerHandler);
        Assert.False(inner.UseCookies);
        Assert.False(inner.AllowAutoRedirect);
    }

    [Fact]
    public async Task APilotInstallationOnAStableChannelThatIsBehindIsUpToDateNotRefused()
    {
        // A technician who moved from pilot to stable while stable is behind has nothing to install and
        // nothing wrong. Refusing, with a warning on every check, would read as an attack.
        var feed = new FakeFeed().Answer(StableManifest, Envelope(_signer, Payload("0.1.5")));

        var status = await Service(feed).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.Contains("newer", status.Sentence, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Lines, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public void PackagesNoNewerThanTheInstalledVersionAreRemovedAtStart()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in (string[])["ScreenTail-0.1.5.msi", "ScreenTail-0.1.10.msi", "ScreenTail-0.1.9.msi.partial", "ScreenTail-0.1.20.msi", "ScreenTail-0.1.20.msi.partial", "seen.json"])
        {
            File.WriteAllText(Path.Combine(_dir, name), "x");
        }

        Service(new FakeFeed()).RemoveStalePackages();

        Assert.Equal(
            ["ScreenTail-0.1.20.msi", "ScreenTail-0.1.20.msi.partial", "seen.json"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        _signer.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private UpdateService Service(HttpMessageHandler feed, IUpdateFolder? folder = null)
    {
        var trust = Trust(_signer.Public);
        var http = new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null, trust)), feed));
        return new UpdateService(http, trust, Installed, folder ?? new PlainUpdateFolder(_dir), () => new UpdatePolicy(UpdateChannel.Stable, false, TimeSpan.FromHours(24), false), () => _busy, signedBuild: true, _log);
    }

    private static PublisherIdentity Identity(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest(subject, key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return PublisherIdentity.From(certificate, timestamped: true);
    }

    private sealed class RecordingFolder(string path) : IUpdateFolder
    {
        public string Path { get; } = path;

        public int Prepared { get; private set; }

        public bool ExistedWhenFirstPrepared { get; private set; }

        public void Prepare()
        {
            if (Prepared++ == 0)
            {
                ExistedWhenFirstPrepared = Directory.Exists(Path);
            }

            _ = Directory.CreateDirectory(Path);
        }
    }

    /// <summary>A body in small chunks with no length, like a chunked response; counts what it handed over.</summary>
    private sealed class ChunkedStream(byte[] data, Action? afterFirstChunk = null) : Stream
    {
        public const int Chunk = 16 * 1024;

        private int _position;

        public long Delivered => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, Chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            var first = _position == 0 && n > 0;
            _position += n;
            if (first)
            {
                afterFirstChunk?.Invoke();
            }

            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The manifest, and a package body made per request. Honours Range with a 206 and its Content-Range
    /// unless told not to; <see cref="Resume"/> overrides how a ranged request is answered.
    /// </summary>
    private sealed class StreamingFeed(byte[] manifest, Func<long?, Stream> package, bool honourRange = true, long? size = null) : HttpMessageHandler
    {
        public List<long> RangesAsked { get; } = [];

        /// <summary>When set, answers a ranged request instead of the honest 206.</summary>
        public Func<long, HttpResponseMessage>? Resume { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != PackageHost)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
            }

            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (from is { } start)
            {
                RangesAsked.Add(start);
                if (Resume is { } resume)
                {
                    return Task.FromResult(resume(start));
                }
            }

            if (from is { } offset && honourRange)
            {
                var content = new StreamContent(package(offset));
                var total = size ?? throw new InvalidOperationException("A feed that honours Range needs the package's size.");
                content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, total - 1, total);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(package(null)) });
        }
    }

    private sealed class BlockingPublisher : IPackagePublisher, IDisposable
    {
        private readonly ManualResetEventSlim _go = new();
        private PublisherIdentity? _answer;

        public ManualResetEventSlim Entered { get; } = new();

        public void Release(PublisherIdentity answer)
        {
            _answer = answer;
            _go.Set();
        }

        public PublisherIdentity? Read(string path, SafeFileHandle file)
        {
            Entered.Set();
            _ = _go.Wait(TimeSpan.FromSeconds(30));
            return _answer;
        }

        public void Dispose()
        {
            _go.Dispose();
            Entered.Dispose();
        }
    }

    private sealed class NeverLauncher : IInstallerLauncher
    {
        public Task<int> RunAsync(string package, CancellationToken ct = default) => throw new InvalidOperationException("Nothing should be launched.");
    }

    private sealed class ExitingLauncher(int code) : IInstallerLauncher
    {
        public Task<int> RunAsync(string package, CancellationToken ct = default) => Task.FromResult(code);
    }

    private sealed class NoHold : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
