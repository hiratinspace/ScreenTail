using ScreenTail.Core.Input;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Input;

/// <summary>
/// When the input hooks are installed (ST-024, ST-031; 2026-09-20 efficiency review).
///
/// A <c>WH_MOUSE_LL</c> hook makes every mouse movement on the machine switch into this process
/// synchronously, and they were installed for the life of the service. A technician records for a
/// fraction of their day, so for the rest of it every mouse move on the desktop was paying a context
/// switch into a process that was going to throw the result away — and the cost does not land as CPU in
/// ScreenTail, it lands as input latency in whatever the technician is actually using.
///
/// <b>The hooks follow the session, not the recording state.</b> Suppression is the obvious alternative
/// and it is wrong: a password field takes focus many times in a session, and uninstalling a global hook
/// on each one is churn in exchange for nothing, because the machine already drops what it sees while
/// suppressed (INV-6). What matters is that there is no session at all for most of the day.
///
/// Nothing is lost by starting late. Sessions begin from a foreground change or a hotkey — neither of
/// which is a click these hooks would have seen — and the recorder already drops the click that starts
/// one as pre-session.
/// </summary>
public sealed class HookLifetimeTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Fact]
    public async Task AnIdleServiceHasNoHooksInstalled()
    {
        // Most of the day. Nothing is recording, so nothing is watching the mouse.
        var hooks = new CountingHooks();
        using var lifetime = await LifetimeAsync(hooks);

        await lifetime.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.False(hooks.Installed);
        Assert.Equal(0, hooks.Installs);
    }

    [Fact]
    public async Task ASessionStartingInstallsThem()
    {
        var hooks = new CountingHooks();
        using var lifetime = await LifetimeAsync(hooks);
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await _harness!.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(hooks.Installed);
    }

    [Fact]
    public async Task ASessionEndingRemovesThem()
    {
        var hooks = new CountingHooks();
        using var lifetime = await LifetimeAsync(hooks);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _harness!.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(await _harness.Machine.DiscardAsync(ct));
        await lifetime.ApplyAsync(ct);

        Assert.False(hooks.Installed);
    }

    [Theory]
    [InlineData(CaptureStateReason.PasswordField)]
    [InlineData(CaptureStateReason.SensitiveContext)]
    public async Task SuppressionDoesNotUninstallThem(CaptureStateReason reason)
    {
        // The churn this design exists to avoid. A password field takes focus many times in a session,
        // and a global hook removed and reinstalled on each one buys nothing: the machine already drops
        // what the hooks see while suppressed (INV-6).
        var hooks = new CountingHooks();
        using var lifetime = await LifetimeAsync(hooks);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _harness!.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(await _harness.Machine.SuppressAsync(reason, ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(hooks.Installed);
        Assert.Equal(1, hooks.Installs);
    }

    [Fact]
    public async Task PausingDoesNotUninstallThemEither()
    {
        var hooks = new CountingHooks();
        using var lifetime = await LifetimeAsync(hooks);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _harness!.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(await _harness.Machine.PauseAsync(ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(hooks.Installed);
        Assert.Equal(1, hooks.Installs);
    }

    [Fact]
    public async Task ApplyingTwiceOverDoesNotReinstall()
    {
        // The loop calls this on every transition, and a session has several. Installing a global hook
        // that is already installed is not free.
        var hooks = new CountingHooks();
        using var lifetime = await LifetimeAsync(hooks);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _harness!.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));

        await lifetime.ApplyAsync(ct);
        await lifetime.ApplyAsync(ct);
        await lifetime.ApplyAsync(ct);

        Assert.Equal(1, hooks.Installs);
    }

    [Fact]
    public async Task HooksThatWillNotInstallAreNotASession()
    {
        // ST-021: the service says what it cannot do rather than pretending. A hook that will not
        // install is reported, and the session carries on recording clicks it can still see.
        var hooks = new CountingHooks { Refuses = true };
        using var lifetime = await LifetimeAsync(hooks);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _harness!.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));

        await lifetime.ApplyAsync(ct);

        Assert.False(hooks.Installed);
        Assert.Equal(1, lifetime.Failures);
    }

    [Fact]
    public async Task HooksComeBackForEverySessionInOneServiceLifetime()
    {
        // T-33: the service starts and stops one hooks object for every session it sees. The second
        // session's loop used to find the first one's stop signal still set, take the hooks out at once,
        // and report them installed anyway. Every session after the first recorded no input.
        var loop = new RestartablePumpTests.FakeLoop();
        await using var pump = new RestartablePump("test hooks", loop.Body);
        _harness = await MachineHarness.StartAsync();
        using var lifetime = new HookLifetime(_harness.Machine, pump.StartAsync, pump.StopAsync);
        var ct = TestContext.Current.CancellationToken;

        for (var session = 0; session < 3; session++)
        {
            Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
            await lifetime.ApplyAsync(ct);

            Assert.True(lifetime.Installed);
            Assert.True(pump.Running);
            Assert.Equal(1, loop.Live);

            Assert.True(await _harness.Machine.DiscardAsync(ct));
            await lifetime.ApplyAsync(ct);

            Assert.False(lifetime.Installed);
            Assert.False(pump.Running);
        }

        Assert.Equal([false, false, false], loop.StoppedOnArrival);
        Assert.Equal(0, lifetime.Failures);
    }

    [Fact]
    public async Task HooksWhoseLoopEndedAreNeverReportedInstalled()
    {
        // The log line "installed" is the first thing anybody reads when nothing is being recorded. A loop
        // that ended before the hooks went in must be counted as a failure, not announced.
        await using var pump = new RestartablePump("test hooks", _ => { });
        _harness = await MachineHarness.StartAsync();
        using var lifetime = new HookLifetime(_harness.Machine, pump.StartAsync, pump.StopAsync);
        var announced = new List<bool>();
        lifetime.Changed += announced.Add;
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));

        await lifetime.ApplyAsync(ct);

        Assert.Empty(announced);
        Assert.False(lifetime.Installed);
        Assert.Equal(1, lifetime.Failures);
    }

    [Fact]
    public async Task ASessionAfterAStopThatTimedOutGetsItsHooksOnceTheOldLoopHasEnded()
    {
        // A stop that times out has still asked the loop to end. Believing the hooks were still in made the
        // next session skip installing them, so it recorded no input even after the old loop had gone. The
        // session that meets the old loop still ending is refused and counted; the one after gets hooks.
        using var release = new ManualResetEventSlim();
        await using var pump = new RestartablePump(
            "test hooks",
            run =>
            {
                run.Ready();
                release.Wait();
                _ = run.Stopping.WaitHandle.WaitOne();
            },
            stopWait: TimeSpan.FromMilliseconds(50));
        _harness = await MachineHarness.StartAsync();
        using var lifetime = new HookLifetime(_harness.Machine, pump.StartAsync, pump.StopAsync);
        var announced = new List<bool>();
        lifetime.Changed += announced.Add;
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);
        Assert.True(await _harness.Machine.DiscardAsync(ct));
        await lifetime.ApplyAsync(ct);

        Assert.False(lifetime.Installed);
        Assert.Equal(1, lifetime.Failures);

        // The old loop is still ending: the start is refused and counted, never announced.
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);
        Assert.False(lifetime.Installed);
        Assert.Equal(2, lifetime.Failures);
        Assert.True(await _harness.Machine.DiscardAsync(ct));
        await lifetime.ApplyAsync(ct);

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => !pump.Running, TimeSpan.FromSeconds(5)));

        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        await lifetime.ApplyAsync(ct);

        Assert.True(lifetime.Installed);
        Assert.True(pump.Running);
        Assert.Equal(2, lifetime.Failures);
        Assert.Equal([true, true], announced);
    }

    private async Task<HookLifetime> LifetimeAsync(CountingHooks hooks)
    {
        _harness = await MachineHarness.StartAsync();
        return new HookLifetime(_harness.Machine, hooks.InstallAsync, hooks.RemoveAsync);
    }

    /// <summary>Stands in for the platform's hooks, and counts what it was asked to do.</summary>
    private sealed class CountingHooks
    {
        public bool Installed { get; private set; }

        public int Installs { get; private set; }

        public int Removals { get; private set; }

        /// <summary>A machine that will not give us a hook, which ST-021 says to report rather than hide.</summary>
        public bool Refuses { get; init; }

        public Task InstallAsync(CancellationToken ct)
        {
            if (Refuses)
            {
                throw new InvalidOperationException("SetWindowsHookEx failed.");
            }

            Installs++;
            Installed = true;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(CancellationToken ct)
        {
            Removals++;
            Installed = false;
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
