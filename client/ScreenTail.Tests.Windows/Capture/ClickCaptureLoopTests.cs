using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Input;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Capture;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Capture;

/// <summary>
/// The drain loop between the hook buffer and the recorder (ST-025). It was untested, and its outer catch
/// read every OperationCanceledException as "stop", so a store call that timed out would have ended click
/// capture for the life of the service with the pill still saying Recording (2026-10-12 review, T-9). A
/// buffer the test writes into stands in for the hooks, and a fake capturer stands in for the screen, so
/// nothing here needs a desktop.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ClickCaptureLoopTests : IAsyncDisposable
{
    private static readonly ScopeDecision InScope = new(CaptureScope.RemoteTool, "rdp", RemoteToolKind.Rdp, "Capturing", 99);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;

    [Fact]
    public async Task ClickCaptureSurvivesATimeoutItDidNotAskFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var machine = await StartRecordingAsync(ct);
        var buffer = new InputRingBuffer();
        var capturer = new FlakyCapturer(failFirst: true);
        var loop = new ClickCaptureLoop(machine, new WindowsInputHooksAccessor(buffer), capturer, () => InScope, NullLogger.Instance);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = loop.RunAsync(stop.Token);

        // The first click's capture throws an OperationCanceledException nobody asked for, which is what a
        // call that timed out throws.
        buffer.Write(new InputSignal(InputKind.Click, Stopwatch.GetTimestamp(), 10, 10));
        await capturer.WaitForCallsAsync(1, ct);

        // Past the click debounce, so the second click is a capture of its own and not one sharing a frame.
        await Task.Delay(new ClickDebouncer().Window + TimeSpan.FromMilliseconds(100), ct);
        buffer.Write(new InputSignal(InputKind.Click, Stopwatch.GetTimestamp(), 20, 20));

        // Were the loop over, this would wait for ever: the second click would stay in the buffer.
        await capturer.WaitForCallsAsync(2, ct);
        Assert.Equal(1, loop.FailedDrains);

        await stop.CancelAsync();
        await running;

        var session = (await _store!.LoadSessionAsync(machine.SessionId!, ct))!;
        Assert.Equal(2, session.Events.OfType<ClickEvent>().Count());
    }

    [Fact]
    public async Task StoppingTheLoopIsStillAStop()
    {
        // The control: the cancellation the host asks for ends the loop, promptly and without a fault.
        var ct = TestContext.Current.CancellationToken;
        var machine = await StartRecordingAsync(ct);
        var loop = new ClickCaptureLoop(machine, new WindowsInputHooksAccessor(new InputRingBuffer()), new FlakyCapturer(failFirst: false), () => InScope, NullLogger.Instance);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = loop.RunAsync(stop.Token);
        await Task.Delay(120, ct);
        await stop.CancelAsync();

        await running.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(running.IsCompletedSuccessfully);
    }

    private async Task<SessionMachine> StartRecordingAsync(CancellationToken ct)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);
        _machine = new SessionMachine(_store, new NoCaptureSources(), new UnavailableDrafter());
        Assert.True(await _machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, ct: ct));
        return _machine;
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

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    /// <summary>Stands in for the screen. Its first capture can throw the way a timed-out call does.</summary>
    private sealed class FlakyCapturer(bool failFirst) : IScreenshotCapturer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public CapturedFrame? CaptureForegroundWindow(nint expected = 0)
        {
            var call = Interlocked.Increment(ref _calls);
            if (failFirst && call == 1)
            {
                throw new OperationCanceledException("a timeout nobody asked for");
            }

            return new CapturedFrame([1, 2, 3, 4], 100, 80, 200, 160, default);
        }

        public byte[]? CaptureSceneGrid(nint expected = 0) => new byte[PerceptualHash.GridLength];

        public async Task WaitForCallsAsync(int count, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Calls < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, ct);
            }

            Assert.True(Calls >= count, $"the capturer was asked {Calls} time(s); {count} expected, so the drain loop is no longer draining");
        }
    }
}
