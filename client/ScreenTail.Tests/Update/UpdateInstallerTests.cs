using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Core.Net;
using ScreenTail.Core.Update;
using static ScreenTail.Tests.Update.TestManifests;

namespace ScreenTail.Tests.Update;

/// <summary>
/// Install (ST-113): never during a session, and the downloaded package checked again, through the handle
/// Windows Installer is started under, immediately before it is started.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private static readonly Uri StableManifest = new(Feed, "stable/win-x64.json");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private readonly TestSigner _signer = new();
    private readonly FakeFeed _feed = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeLauncher _launcher = new();
    private readonly PublisherIdentity _ours = Identity("CN=ScreenTail Test, O=ScreenTail Test, C=US");
    private bool _busy;

    [Fact]
    public async Task AReadyUpdateIsHandedToWindowsInstallerWhileItsFileIsHeldOpen()
    {
        var (updates, installer) = await ReadyAsync();
        var path = updates.Ready!.Path;

        // Another leaf for the same publisher, as next week's Artifact Signing certificate would be.
        _publisher.Answer = Identity("CN=ScreenTail Test, O=ScreenTail Test, C=US");

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.True(answer.Ok, answer.Refusal);
        Assert.Equal(path, _launcher.Package);
        Assert.True(installer.Installing);
        Assert.Equal(UpdateState.Installing, updates.Status.State);

        // The handle the signature was checked through is still open while Windows Installer runs, and
        // no session may start meanwhile.
        Assert.NotNull(_publisher.Handle);
        Assert.False(_publisher.Handle!.IsClosed);
        Assert.Equal(1, _holds);

        _launcher.Exit(0);
        await installer.Finished;
        Assert.False(installer.Installing);
        Assert.True(_publisher.Handle.IsClosed);
        Assert.Equal(0, _holds);
    }

    [Fact]
    public async Task AFailedCheckReleasesTheHoldOnNewSessions()
    {
        var (_, installer) = await ReadyAsync();
        _publisher.Answer = Identity("CN=Somebody Else, O=Somebody Else, C=US");

        _ = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _holds);
        Assert.False(installer.Installing);
    }

    [Fact]
    public async Task NothingIsInstalledWhileASessionIsRecording()
    {
        var (updates, installer) = await ReadyAsync();
        _busy = true;

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal(UpdateInstaller.DuringSession, answer.Refusal);
        Assert.Null(_launcher.Package);
        Assert.Equal(UpdateState.Ready, updates.Status.State);
    }

    [Fact]
    public async Task WithNothingReadyNothingIsStarted()
    {
        var updates = Service();
        var installer = Installer(updates);

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateInstaller.NothingReady, answer.Refusal);
        Assert.Null(_launcher.Package);
    }

    [Fact]
    public async Task APackageChangedOnDiskSinceItsDownloadIsRefusedAndRemoved()
    {
        // It sat in a folder of the technician's in between. Anything running as them could have swapped it.
        var (updates, installer) = await ReadyAsync();
        var path = updates.Ready!.Path;
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes[100] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Null(_launcher.Package);
        Assert.Null(_publisher.Handle);
        Assert.False(File.Exists(path));
        Assert.Equal(UpdateState.Refused, updates.Status.State);
        Assert.Null(updates.Ready);
    }

    [Fact]
    public async Task APackageWithAnotherPublisherIsRefused()
    {
        var (updates, installer) = await ReadyAsync();
        _publisher.Answer = Identity("CN=Somebody Else, O=Somebody Else, C=US");

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Null(_launcher.Package);
        Assert.Equal(UpdateState.Refused, updates.Status.State);
    }

    [Fact]
    public async Task APackageWindowsDoesNotAcceptIsRefused()
    {
        var (_, installer) = await ReadyAsync();
        _publisher.Answer = null;

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Null(_launcher.Package);
    }

    [Fact]
    public async Task ADevelopmentBuildInstallsNothing()
    {
        var (_, installer) = await ReadyAsync(developmentBuild: true);
        _publisher.Answer = _ours;

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Null(_launcher.Package);
    }

    [Fact]
    public async Task CancellingTheAdministratorPromptLeavesTheUpdateReady()
    {
        // 1602: the technician (or the administrator they asked) said no at the UAC prompt.
        var (updates, installer) = await ReadyAsync();
        _publisher.Answer = _ours;

        _ = await installer.InstallAsync(TestContext.Current.CancellationToken);
        _launcher.Exit(1602);
        await installer.Finished;

        Assert.False(installer.Installing);
        Assert.Equal(UpdateState.Ready, updates.Status.State);
        Assert.Contains("cancel", updates.Status.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(updates.Ready);
    }

    [Fact]
    public async Task ASecondInstallWhileTheFirstRunsIsRefused()
    {
        var (_, installer) = await ReadyAsync();
        _publisher.Answer = _ours;

        Assert.True((await installer.InstallAsync(TestContext.Current.CancellationToken)).Ok);
        var second = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(second.Ok);
        Assert.Equal(1, _launcher.Starts);
        _launcher.Exit(0);
        await installer.Finished;
    }

    public void Dispose()
    {
        _signer.Dispose();
        _launcher.Exit(0);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private async Task<(UpdateService Updates, UpdateInstaller Installer)> ReadyAsync(bool developmentBuild = false)
    {
        _ = _feed.Answer(StableManifest, Envelope(_signer, Payload())).Answer(PackageUrl(), Package);
        var updates = Service();
        Assert.Equal(UpdateState.Ready, (await updates.CheckAsync(TestContext.Current.CancellationToken)).State);
        return (updates, Installer(updates, developmentBuild ? null : _ours));
    }

    private UpdateService Service()
    {
        var trust = Trust(_signer.Public);
        var http = new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null, trust)), _feed));
        return new UpdateService(http, trust, Installed, new PlainUpdateFolder(_dir), () => new UpdatePolicy(UpdateChannel.Stable, false, TimeSpan.FromHours(24), false), () => _busy, signedBuild: true);
    }

    private UpdateInstaller Installer(UpdateService updates) => Installer(updates, _ours);

    private UpdateInstaller Installer(UpdateService updates, PublisherIdentity? running) =>
        new(updates, _publisher, _launcher, running, _ => Task.FromResult<IDisposable?>(_busy ? null : new Hold(this)));

    /// <summary>How many holds on new sessions are in force: the session machine's answer, counted.</summary>
    private int _holds;

    private sealed class Hold : IDisposable
    {
        private UpdateInstallerTests? _owner;

        public Hold(UpdateInstallerTests owner)
        {
            _owner = owner;
            _owner._holds++;
        }

        public void Dispose()
        {
            if (_owner is { } owner)
            {
                owner._holds--;
                _owner = null;
            }
        }
    }

    private static PublisherIdentity Identity(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest(subject, key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return PublisherIdentity.From(certificate, timestamped: true);
    }

    private sealed class FakePublisher : IPackagePublisher
    {
        public PublisherIdentity? Answer { get; set; }

        public SafeFileHandle? Handle { get; private set; }

        public PublisherIdentity? Read(string path, SafeFileHandle file)
        {
            Handle = file;
            return Answer;
        }
    }

    private sealed class FakeLauncher : IInstallerLauncher
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Package { get; private set; }

        public int Starts { get; private set; }

        public void Exit(int code) => _exit.TrySetResult(code);

        public Task<int> RunAsync(string package, CancellationToken ct = default)
        {
            Starts++;
            Package = package;
            return _exit.Task;
        }
    }
}
