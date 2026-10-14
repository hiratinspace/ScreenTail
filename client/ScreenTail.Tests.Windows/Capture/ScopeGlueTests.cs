using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Capture;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Capture;

/// <summary>
/// The service's scene loop and mark-moment sources still go through the scope gate (ST-026, INV-5).
///
/// The gates themselves are proved on any OS by ScopeRecordingTests, against the Core classes. These pin
/// the Windows glue that hands work to them, which is the one place a later edit could route a sample or a
/// marked moment around the gate without a Mac test noticing (2026-10-12 review, T-10). A fake capturer
/// stands in for the screen, so nothing here depends on what is on the desktop.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ScopeGlueTests : IAsyncDisposable
{
    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;

    [Fact]
    public async Task MarkingAMomentOverAnExcludedWindowTakesNoPicture()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturer = new FakeCapturer();
        var sources = new WindowsCaptureSources(capturer, () => Excluded, () => { }, NullLogger.Instance);
        var machine = await StartAsync(sources, ct);

        Assert.True(await machine.MarkMomentAsync(ct));

        Assert.Equal(0, capturer.Captures);
        var stored = (await _store!.LoadSessionAsync(machine.SessionId!, ct))!;
        Assert.Single(stored.Events.OfType<MarkerEvent>());
    }

    [Fact]
    public async Task MarkingAMomentInScopeTakesThePictureOfTheJudgedWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturer = new FakeCapturer();
        var sources = new WindowsCaptureSources(capturer, () => InScope, () => { }, NullLogger.Instance);
        var machine = await StartAsync(sources, ct);

        Assert.True(await machine.MarkMomentAsync(ct));

        Assert.Equal(1, capturer.Captures);
        Assert.Equal(99, capturer.Expected);
    }

    [Fact]
    public async Task TheSceneLoopNeverLooksAtAnExcludedWindow()
    {
        // Run until scope has been asked several times, which proves samples were taken, then stop.
        var ct = TestContext.Current.CancellationToken;
        var capturer = new FakeCapturer();
        var asked = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScopeDecision? Scope()
        {
            if (Interlocked.Increment(ref asked) >= 5)
            {
                enough.TrySetResult();
            }

            return Excluded;
        }

        var machine = await StartAsync(new NoCaptureSources(), ct);
        var loop = new SceneSampleLoop(machine, capturer, Scope, NullLogger.Instance, Fast());

        await RunUntilAsync(loop, enough.Task, ct);

        Assert.Equal(0, capturer.Grids);
        Assert.Equal(0, capturer.Captures);
    }

    [Fact]
    public async Task TheSceneLoopLooksAtAnInScopeWindow()
    {
        // The control for the test above: the same loop, allowed, does look, and at the window judged.
        var ct = TestContext.Current.CancellationToken;
        var capturer = new FakeCapturer();
        var machine = await StartAsync(new NoCaptureSources(), ct);
        var loop = new SceneSampleLoop(machine, capturer, () => InScope, NullLogger.Instance, Fast());

        await RunUntilAsync(loop, capturer.Looked, ct);

        Assert.True(capturer.Grids >= 1);
        Assert.Equal(99, capturer.GridExpected);
    }

    public async ValueTask DisposeAsync()
    {
        if (_machine is not null)
        {
            await _machine.DisposeAsync();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    /// <summary>A decision that forbids frames yet names a real window, so only the scope rule can refuse.</summary>
    private static ScopeDecision Excluded => new(CaptureScope.Excluded, null, null, "Not capturing.", 77);

    private static ScopeDecision InScope => new(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing ScreenConnect.", 99);

    private static SceneSampler Fast() =>
        new(options: new SceneSamplerOptions { SampleEvery = TimeSpan.FromMilliseconds(10), MinimumGap = TimeSpan.Zero });

    private static async Task RunUntilAsync(SceneSampleLoop loop, Task until, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = loop.RunAsync(stop.Token);
        await until.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await stop.CancelAsync();
        await running;
    }

    private async Task<SessionMachine> StartAsync(ICaptureSources sources, CancellationToken ct)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);
        _machine = new SessionMachine(_store, sources, new UnavailableDrafter());
        Assert.True(await _machine.StartAsync(ScreenConnect, ct: ct));
        return _machine;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    /// <summary>Stands in for the screen grab and remembers which window it was told to expect.</summary>
    private sealed class FakeCapturer : IScreenshotCapturer
    {
        private readonly TaskCompletionSource _looked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _grids;
        private int _captures;

        public int Grids => Volatile.Read(ref _grids);

        public int Captures => Volatile.Read(ref _captures);

        public nint Expected { get; private set; }

        public nint GridExpected { get; private set; }

        public Task Looked => _looked.Task;

        public CapturedFrame? CaptureForegroundWindow(nint expected = 0)
        {
            Expected = expected;
            Interlocked.Increment(ref _captures);
            return new CapturedFrame([1, 2, 3, 4], 100, 80, 200, 160, default);
        }

        public byte[]? CaptureSceneGrid(nint expected = 0)
        {
            GridExpected = expected;
            Interlocked.Increment(ref _grids);
            _looked.TrySetResult();
            return new byte[PerceptualHash.GridLength];
        }
    }
}
