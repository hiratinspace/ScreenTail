using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ScreenTail.Core.Net;
using ScreenTail.Core.Update;
using static ScreenTail.Tests.Update.TestManifests;

namespace ScreenTail.Tests.Update;

/// <summary>
/// The check and the background download (ST-113 AC1, AC2), through the real egress guard: what is sent,
/// when nothing is, what is believed and what is logged.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private static readonly Uri Backend = new("https://api.screentail.example/");
    private static readonly Uri StableManifest = new(Feed, "stable/win-x64.json");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private readonly TestSigner _signer = new();
    private readonly FakeFeed _feed = new();
    private readonly ListLogger _log = new();
    private bool _busy;
    private UpdatePolicy _policy = new(UpdateChannel.Stable, false, TimeSpan.FromHours(24), false);

    [Fact]
    public async Task AnUnconfiguredBuildMakesNoRequestAtAll()
    {
        var updates = Service(trust: UpdateTrust.None);

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.NotConfigured, status.State);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    public async Task ABuildNobodySignedMakesNoRequestAtAll()
    {
        var updates = Service(signed: false);

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.DevelopmentBuild, status.State);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    public async Task AnAdminWhoTurnedChecksOffGetsNoRequest()
    {
        _policy = _policy with { Every = null };

        var status = await Service().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Off, status.State);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    public async Task ACheckIsOneBareGetForTheChannelsManifestUnderItsOwnPurpose()
    {
        // No credential, no cookie, no device, tenant or version, no body: the channel and the architecture
        // in the path are all the feed learns.
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload("0.1.10")));

        var status = await Service().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.UpToDate, status.State);
        var request = Assert.Single(_feed.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(StableManifest.AbsoluteUri, request.Url);
        Assert.Equal(EgressPurpose.UpdateCheck, request.Purpose);
        Assert.Empty(request.Headers);
        Assert.False(request.HasBody);
    }

    [Fact]
    public async Task ThePilotChannelAsksForThePilotManifest()
    {
        _policy = _policy with { Channel = UpdateChannel.Pilot };

        _ = await Service().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri(Feed, "pilot/win-x64.json").AbsoluteUri, Assert.Single(_feed.Requests).Url);
    }

    [Fact]
    public async Task UnderLocalOnlyTheGuardStopsTheCheckAndNothingLeaves()
    {
        var updates = Service(localOnly: true);

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Blocked, status.State);
        Assert.Contains("Local-only", status.Sentence, StringComparison.Ordinal);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    public async Task ANewerReleaseIsDownloadedInTheBackgroundAndHeldReady()
    {
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), Package);
        var updates = Service();

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Ready, status.State);
        Assert.Equal("0.1.20", status.AvailableVersion);
        Assert.Equal([StableManifest.AbsoluteUri, PackageUrl().AbsoluteUri], _feed.Urls);
        var download = _feed.Requests[1];
        Assert.Equal(EgressPurpose.UpdateCheck, download.Purpose);
        Assert.Empty(download.Headers);
        var ready = updates.Ready!;
        Assert.StartsWith(_dir, ready.Path, StringComparison.Ordinal);
        Assert.Equal(Package, await File.ReadAllBytesAsync(ready.Path, TestContext.Current.CancellationToken));
        Assert.Equal([Path.GetFileName(ready.Path)], Directory.GetFiles(_dir, "*.msi*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task WhileASessionIsRecordingNothingIsDownloaded()
    {
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), Package);
        _busy = true;
        var updates = Service();

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, status.State);
        Assert.Equal([StableManifest.AbsoluteUri], _feed.Urls);
        Assert.Null(updates.Ready);

        _busy = false;
        var after = await updates.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Ready, after.State);
    }

    [Fact]
    public async Task AManifestWhoseSignatureDoesNotVerifyIsRefusedAndLogged()
    {
        // AC2: "signature mismatch rejected and logged".
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload(), sign: _ => new byte[64])).Answer(PackageUrl(), Package);
        var updates = Service();

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Refused, status.State);
        Assert.Null(updates.Ready);
        Assert.Equal([StableManifest.AbsoluteUri], _feed.Urls);
        var line = Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);
        Assert.Contains("signature", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageThatIsNotTheOneTheManifestDescribesIsRefusedDeletedAndLogged()
    {
        var other = (byte[])Package.Clone();
        other[^1] ^= 0xFF;
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), other);
        var updates = Service();

        var status = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Refused, status.State);
        Assert.Null(updates.Ready);
        Assert.Empty(Directory.GetFiles(_dir, "*.msi*"));
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APackageLongerThanItsManifestSaysIsRefused()
    {
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), [.. Package, .. new byte[1024]]);

        var status = await Service().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Refused, status.State);
        Assert.Empty(Directory.GetFiles(_dir, "*.msi*"));
    }

    [Fact]
    public async Task AnOldManifestReplayedAfterANewerOneIsRefused()
    {
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload("0.1.30"))).Answer(PackageUrl("0.1.30"), Package);
        var updates = Service();
        _ = await updates.CheckAsync(TestContext.Current.CancellationToken);

        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload("0.1.20")));
        var replayed = await Service().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Refused, replayed.State);
    }

    [Fact]
    public async Task AFeedThatDoesNotAnswerIsAFailureToTryAgainNotARefusal()
    {
        var status = await Service().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Failed, status.State);
    }

    [Fact]
    public async Task NothingIsAskedBeforeThisRunHasFetchedTheTenantsPolicy()
    {
        // Until then the policy in force is the cached copy on disk, which the technician can edit, so an
        // update check could otherwise leave under a local-only the tenant had locked (INV-8, INV-11).
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload("0.1.10")));
        var updates = Service();
        var fetched = new TaskCompletionSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var checkedOnce = new TaskCompletionSource();
        updates.Changed += status =>
        {
            if (status.State == UpdateState.UpToDate)
            {
                checkedOnce.TrySetResult();
            }
        };

        var running = updates.RunAsync(fetched.Task, stop.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(_feed.Requests);

        fetched.SetResult();
        await checkedOnce.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(_feed.Requests);

        await stop.CancelAsync();
        await running;
    }

    public void Dispose()
    {
        _signer.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private UpdateService Service(UpdateTrust? trust = null, bool signed = true, bool localOnly = false)
    {
        trust ??= Trust(_signer.Public);
        var egress = new EgressPolicy(ClientEgress.Settings(Backend, trust));
        egress.Apply(localOnly, enforced: false);
        var http = new HttpClient(new EgressGuard(egress, _feed));
        return new UpdateService(http, trust, Installed, new PlainUpdateFolder(_dir), () => _policy, () => _busy, signed, _log);
    }
}

/// <summary>Every line logged, with its level, as the scrubbing provider would receive it.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Text)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (Lines)
        {
            Lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
