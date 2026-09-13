using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-041, the half of the login criterion that is about the product rather than the class: the worker
/// raising <c>SensitiveContextSeen</c> has to end with capture actually off (INV-6). Before this guard the
/// event went nowhere, so a sign-in screen was marked and then screenshotted again on the next click.
/// </summary>
public sealed class SensitiveContextGuardTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private static readonly DateTimeOffset At = new(2026, 9, 13, 13, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(At);
    private MachineHarness? _harness;

    [Fact]
    public async Task ASightingStopsCaptureAndTheWindowEndingStartsItAgain()
    {
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(guard.Holding);
        Assert.Equal(1, guard.Holds);

        // Still inside the ten seconds: capture stays off.
        _time.Advance(LoginScreenHeuristic.Suppression - TimeSpan.FromSeconds(1));
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Suppressed, machine.State);

        _time.Advance(TimeSpan.FromSeconds(2));
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Recording, machine.State);
        Assert.False(guard.Holding);
        Assert.Equal(1, guard.Holds);
    }

    [Fact]
    public async Task ALoginScreenThatStaysOnScreenKeepsCaptureOff()
    {
        // The case a restarting timer would get wrong: sightings keep arriving, and each one has to push
        // the window out rather than start a second hold that races the first one's resume.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);

        for (var i = 0; i < 5; i++)
        {
            guard.Seen(LoginScreenHeuristic.Suppression);
            await guard.TickAsync(TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(6));
            await guard.TickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SessionState.Suppressed, machine.State);
        }

        Assert.Equal(1, guard.Holds);

        _time.Advance(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task TheGuardDoesNotResumeASessionTheTechnicianPaused()
    {
        // Suppressed and paused are different states with different owners. If the technician pauses while
        // a hold is running, the window ending must not hand capture back — only they can do that.
        var machine = await RecordingAsync();
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(await machine.PauseAsync(TestContext.Current.CancellationToken));

        _time.Advance(LoginScreenHeuristic.Suppression * 2);
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionState.Paused, machine.State);
        Assert.False(guard.Holding);
    }

    [Fact]
    public async Task ASightingWhileNotRecordingHoldsNothing()
    {
        // Nothing to suppress, so nothing to resume later. Claiming a hold here would make the next tick
        // unsuppress a session this guard never touched.
        var machine = await RecordingAsync();
        Assert.True(await machine.PauseAsync(TestContext.Current.CancellationToken));
        var guard = new SensitiveContextGuard(machine, _time);

        guard.Seen(LoginScreenHeuristic.Suppression);
        await guard.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(guard.Holding);
        Assert.Equal(0, guard.Holds);
        Assert.Equal(SessionState.Paused, machine.State);
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null));
        return _harness.Machine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
