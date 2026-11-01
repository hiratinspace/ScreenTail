using ScreenTail.Core.Privacy;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// ST-031 over ST-040: the focus hook is in while the password-field guard could decide something and out
/// otherwise, against the real state machine. With no session it heard every control on the machine take
/// focus and woke two threads each time to decide nothing.
/// </summary>
public sealed class FocusInterestTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private readonly List<bool> _told = [];
    private MachineHarness? _harness;

    [Fact]
    public async Task AnIdleServiceDoesNotListen()
    {
        _harness = await MachineHarness.StartAsync();
        using var interest = new FocusInterest(_harness.Machine, _told.Add);

        Assert.Equal([false], _told);
        Assert.False(interest.Listening);
    }

    [Fact]
    public async Task ASessionStartingPutsTheHookInAndItsEndTakesItOut()
    {
        _harness = await MachineHarness.StartAsync();
        var machine = _harness.Machine;
        var ct = TestContext.Current.CancellationToken;
        using var interest = new FocusInterest(machine, _told.Add);

        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));
        Assert.True(interest.Listening);

        Assert.True(await machine.StopAsync(ct));
        Assert.False(SessionLoop.IsUnderWay(machine.State), $"the session was still under way in {machine.State}");

        // Told once each way, never twice in a row with the same answer.
        Assert.Equal([false, true, false], _told);
    }

    [Fact]
    public async Task APausedOrSuppressedSessionKeepsListening()
    {
        // A paused session is one Resume away from recording: focus arriving at a field during the pause
        // has to take a hold, and leaving one has to let it go (PasswordFieldGuard, T-41).
        _harness = await MachineHarness.StartAsync();
        var machine = _harness.Machine;
        var ct = TestContext.Current.CancellationToken;
        using var interest = new FocusInterest(machine, _told.Add);
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));

        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, ct));
        Assert.True(interest.Listening);
        Assert.True(await machine.PauseAsync(ct));
        Assert.True(interest.Listening);
        Assert.True(await machine.ResumeAsync(ct));
        Assert.True(interest.Listening);

        Assert.Equal([false, true], _told);
    }

    [Fact]
    public async Task AServiceThatStartsInsideASessionListensFromTheStart()
    {
        _harness = await MachineHarness.StartAsync();
        var machine = _harness.Machine;
        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: TestContext.Current.CancellationToken));

        using var interest = new FocusInterest(machine, _told.Add);

        Assert.Equal([true], _told);
    }

    [Fact]
    public async Task OnceDisposedItIsToldNothingMore()
    {
        _harness = await MachineHarness.StartAsync();
        var machine = _harness.Machine;
        var interest = new FocusInterest(machine, _told.Add);
        interest.Dispose();

        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: TestContext.Current.CancellationToken));

        Assert.Equal([false], _told);
    }

    [Fact]
    public async Task TheGuardSeesTheFieldAtSessionStartWithoutTheHook()
    {
        // The hook comes back with the session, a moment after it. The guard does not wait for it: it
        // wakes on the same transition and asks, so a field already focused is held with no focus event
        // at all. The poll is out of reach, so only the transition can have woken it.
        _harness = await MachineHarness.StartAsync();
        var machine = _harness.Machine;
        var ct = TestContext.Current.CancellationToken;
        using var interest = new FocusInterest(machine, _told.Add);
        var probe = new PasswordProbe();
        using var guard = new PasswordFieldGuard(machine, probe, options: new PasswordFieldOptions { PollEvery = TimeSpan.FromMinutes(5) });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = guard.RunAsync(stopping.Token);

        Assert.True(await machine.StartAsync(Rdp, localOnly: false, policyVersion: null, ct: ct));

        for (var i = 0; i < 100 && machine.State != SessionState.Suppressed; i++)
        {
            await Task.Delay(20, ct);
        }

        Assert.Equal(SessionState.Suppressed, machine.State);
        Assert.True(interest.Listening);
        await stopping.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ASessionStartingWhileItIsBeingBuiltIsNotMissed()
    {
        // Review of #252: the service builds this while the pipe, the hotkeys and the coordinator already
        // run, so a session can start at any moment of the constructor. Here it starts inside the first
        // answer's callback, the last moment before the old code subscribed, which left the hook out for
        // the whole session.
        _harness = await MachineHarness.StartAsync();
        var machine = _harness.Machine;
        var started = false;
        using var interest = new FocusInterest(machine, wanted =>
        {
            _told.Add(wanted);
            if (!started)
            {
                started = true;
                Assert.True(machine.StartAsync(Rdp, localOnly: false, policyVersion: null).GetAwaiter().GetResult());
            }
        });

        Assert.True(interest.Listening);
        Assert.Equal([false, true], _told);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    private sealed class PasswordProbe : IFocusedFieldProbe
    {
        public FocusedField Read() => FocusedField.Password;
    }
}
