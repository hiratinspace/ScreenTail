using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTrail.Core.Detection;
using ScreenTrail.Core.Detection.Registry;
using ScreenTrail.Core.History;
using ScreenTrail.Core.Sessions;
using ScreenTrail.Core.Store;
using ScreenTrail.Service.Detection;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Tests.Windows.Detection;

/// <summary>
/// ST-079's denominator through the coordinator (spec v0.7.3 S4): the remote sessions the service sees and
/// does not capture reach the store with the reason the coordinator had, and a captured one does not.
/// The counting rules themselves are <c>RemoteVisitsTests</c> on any OS; this holds the wiring.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class CoverageCountingTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly RemoteToolRegistry Shipped = RemoteToolRegistry.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"st-coverage-{Guid.NewGuid():N}.db");
    private readonly ManualTime _clock = new(At);
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;

    [Fact]
    public async Task ARemoteSessionWithAutoStartOffIsCountedOnceAsMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, autoStart: () => false);

        // An hour in Remote Desktop with trips to Outlook, never captured: one miss, for auto-start.
        for (var i = 0; i < 12; i++)
        {
            await coordinator.HandleAsync(Rdp(), ct);
            await TickForAsync(coordinator, TimeSpan.FromMinutes(4), ct);
            await coordinator.HandleAsync(Outlook(), ct);
            await TickForAsync(coordinator, TimeSpan.FromSeconds(60), ct);
        }

        await TickForAsync(coordinator, TimeSpan.FromSeconds(120), ct);

        Assert.Equal(SessionState.Idle, _machine!.State);
        var counts = await _store!.CountCoverageAsync(At, ct);
        Assert.Equal(1, Assert.Single(counts.Missed, m => m.Key == ("rdp", MissReason.AutoStartOff)).Value);
        Assert.Empty(counts.Captured);
    }

    [Fact]
    public async Task AToolSwitchedOffIsMissedForThatReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, options: new ScopeOptions { DisabledTools = new HashSet<string>(["rdp"], StringComparer.OrdinalIgnoreCase) });

        await coordinator.HandleAsync(Rdp(), ct);
        await TickForAsync(coordinator, TimeSpan.FromMinutes(5), ct);
        await coordinator.HandleAsync(Outlook(), ct);
        await TickForAsync(coordinator, TimeSpan.FromSeconds(120), ct);

        var counts = await _store!.CountCoverageAsync(At, ct);
        Assert.True(counts.Missed.ContainsKey(("rdp", MissReason.ToolSwitchedOff)));
    }

    [Fact]
    public async Task NothingOnScreenToSayCaptureIsOnIsMissedForThatReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, indicated: () => false);

        await coordinator.HandleAsync(Rdp(), ct);
        await TickForAsync(coordinator, TimeSpan.FromMinutes(5), ct);
        await coordinator.HandleAsync(Outlook(), ct);
        await TickForAsync(coordinator, TimeSpan.FromSeconds(120), ct);

        var counts = await _store!.CountCoverageAsync(At, ct);
        Assert.True(counts.Missed.ContainsKey(("rdp", MissReason.NoIndicator)));
    }

    [Fact]
    public async Task AStartHeldForAnUpdateIsMissedForThatReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);
        using var hold = await _machine!.HoldStartsAsync(ct);

        await coordinator.HandleAsync(Rdp(), ct);
        await TickForAsync(coordinator, TimeSpan.FromMinutes(5), ct);
        await coordinator.HandleAsync(Outlook(), ct);
        await TickForAsync(coordinator, TimeSpan.FromSeconds(120), ct);

        var counts = await _store!.CountCoverageAsync(At, ct);
        Assert.True(counts.Missed.ContainsKey(("rdp", MissReason.StartHeld)));
    }

    [Fact]
    public async Task ACapturedRemoteSessionIsNoMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Rdp(), ct);
        Assert.Equal(SessionState.Recording, _machine!.State);
        await TickForAsync(coordinator, TimeSpan.FromMinutes(5), ct);
        await coordinator.HandleAsync(Outlook(), ct);
        await TickForAsync(coordinator, TimeSpan.FromSeconds(120), ct);

        var counts = await _store!.CountCoverageAsync(At, ct);
        Assert.Empty(counts.Missed);
        Assert.Equal(1, counts.Captured["rdp"]);
    }

    [Fact]
    public async Task ARemoteSessionInProgressAtShutdownIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, autoStart: () => false);
        using var stopping = new CancellationTokenSource();

        var running = coordinator.RunAsync(stopping.Token);
        await coordinator.HandleAsync(Rdp(), ct);
        await TickForAsync(coordinator, TimeSpan.FromMinutes(5), ct);
        await stopping.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // How the loops end when the service stops.
        }

        var counts = await _store!.CountCoverageAsync(At, ct);
        Assert.True(counts.Missed.ContainsKey(("rdp", MissReason.AutoStartOff)));
    }

    private async Task TickForAsync(AutoSessionCoordinator coordinator, TimeSpan span, CancellationToken ct)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(5))
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            await coordinator.TickOnceAsync(ct);
        }
    }

    private async Task<AutoSessionCoordinator> CoordinatorAsync(
        CancellationToken ct,
        Func<bool>? indicated = null,
        Func<bool>? autoStart = null,
        ScopeOptions? options = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), _clock, ct);
        _machine = new SessionMachine(_store, new NoCaptureSources(), new UnavailableDrafter(), time: _clock, options: new SessionMachineOptions { EmptySessionMinimum = TimeSpan.Zero });
        var policy = new ScopePolicy(Shipped, options);
        var trigger = new SessionTrigger(policy, _clock, TimeSpan.FromSeconds(90));
        return new AutoSessionCoordinator(
            _machine,
            policy,
            trigger,
            indicated ?? (() => true),
            NullLogger.Instance,
            () => (false, "default"),
            autoStart: autoStart,
            time: _clock,
            visits: new RemoteVisits(() => trigger.Grace, time: _clock),
            missed: _store.RecordMissedVisitAsync);
    }

    private static ForegroundWindowInfo Rdp() => new(111, 100, "mstsc", "Remote Desktop", "Window", null, false, At);

    private static ForegroundWindowInfo Outlook() => new(222, 200, "outlook", "Inbox", "Window", null, false, At);

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
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

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
