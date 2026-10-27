using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Input;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Capture;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests;

namespace ScreenTail.Tests.Windows.Capture;

/// <summary>
/// ST-031: the click drain and the scene sampler sleep while there is no session, and wake for one.
///
/// The drain looked at an empty buffer twenty times a second for the life of the service, and the sampler
/// asked once a second whether a session was recording, on a machine that records for a fraction of the
/// day. The hooks that fill the buffer are out with no session (HookLifetime), so there was nothing to
/// drain. Counted timers rather than timings (<see cref="CountingTime"/>); nothing here needs a desktop.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class IdleLoopTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;

    [Fact]
    public async Task TheClickDrainTicksOnlyWhileThereIsASession()
    {
        var ct = TestContext.Current.CancellationToken;
        var machine = await MachineAsync(ct);
        var time = new CountingTime();
        var loop = new ClickCaptureLoop(machine, new WindowsInputHooksAccessor(new InputRingBuffer()), new NothingOnScreen(), () => null, NullLogger.Instance, time);

        await AssertTicksOnlyWithASessionAsync(machine, time, loop.RunAsync, ct);
    }

    [Fact]
    public async Task TheSceneSamplerTicksOnlyWhileThereIsASession()
    {
        var ct = TestContext.Current.CancellationToken;
        var machine = await MachineAsync(ct);
        var time = new CountingTime();
        var loop = new SceneSampleLoop(machine, new NothingOnScreen(), () => null, NullLogger.Instance, time: time);

        await AssertTicksOnlyWithASessionAsync(machine, time, loop.RunAsync, ct);
    }

    private static async Task AssertTicksOnlyWithASessionAsync(SessionMachine machine, CountingTime time, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = run(stopping.Token);

        await Task.Delay(300, ct);
        Assert.Equal(0, time.Created);

        Assert.True(await machine.StartAsync(Rdp, ct: ct));
        Assert.True(await UntilAsync(() => time.Live == 1), "no timer was armed for the session");

        Assert.True(await machine.StopAsync(ct));
        Assert.True(await UntilAsync(() => time.Live == 0), "the timer outlived the session");

        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(1, time.Created);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private async Task<SessionMachine> MachineAsync(CancellationToken ct)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);
        _machine = new SessionMachine(_store, new NoCaptureSources(), new UnavailableDrafter());
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

    private sealed class NothingOnScreen : IScreenshotCapturer
    {
        public CapturedFrame? CaptureForegroundWindow(nint expected = 0) => null;

        public byte[]? CaptureSceneGrid(nint expected = 0) => null;
    }
}
