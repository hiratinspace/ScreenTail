using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// The redaction backlog is told when it moves, not only with the next state change (owner's report,
/// 2026-11-05: "the values never change on the small icon"). The pill's "N redacting" count stood at its
/// value from the session's last transition, because the machine kept the worker's count and said it only
/// inside the next state snapshot.
/// </summary>
public sealed class CountsChangedTests
{
    private static readonly RemoteTool Other = new() { Kind = RemoteToolKind.Other };

    [Fact]
    public async Task ABacklogThatMovesIsToldWithTheStateAsItIs()
    {
        await using var harness = await MachineHarness.StartAsync();
        var machine = harness.Machine;
        Assert.True(await machine.StartAsync(Other, ct: TestContext.Current.CancellationToken));
        var told = new List<CaptureStateSnapshot>();
        var transitions = 0;
        machine.CountsChanged += told.Add;
        machine.StateChanged += _ => transitions++;

        machine.ReportPendingRedactions(3);
        machine.ReportPendingRedactions(0);

        Assert.Equal([3, 0], told.Select(s => s.PendingRedactions));
        Assert.All(told, s => Assert.Equal(CaptureStates.Recording, s.State));
        Assert.All(told, s => Assert.Equal(machine.SessionId, s.SessionId));

        // A count is not a transition: the guards, the hooks and the session loop are not woken for it.
        Assert.Equal(0, transitions);
    }

    [Fact]
    public async Task TheSameCountAgainIsNotTold()
    {
        await using var harness = await MachineHarness.StartAsync();
        var told = 0;
        harness.Machine.CountsChanged += _ => told++;

        harness.Machine.ReportPendingRedactions(2);
        harness.Machine.ReportPendingRedactions(2);

        Assert.Equal(1, told);
    }

    [Fact]
    public async Task ACountThatMovesDuringATransitionIsToldAfterItWithTheNewState()
    {
        // INV-4: a count's snapshot must never be overtaken by a transition. Told out of order, a
        // "recording" taken before a pause would reach the pill after the pause, or the other way round,
        // and the pill would say the wrong thing with nothing coming to correct it.
        await using var harness = await MachineHarness.StartAsync();
        var machine = harness.Machine;
        Assert.True(await machine.StartAsync(Other, ct: TestContext.Current.CancellationToken));
        var log = new List<string>();
        machine.StateChanged += snapshot =>
        {
            // The worker reports while the pause is still being told.
            machine.ReportPendingRedactions(4);
            log.Add($"state {snapshot.State}");
        };
        machine.CountsChanged += snapshot => log.Add($"counts {snapshot.State} {snapshot.PendingRedactions}");

        Assert.True(await machine.PauseAsync(TestContext.Current.CancellationToken));
        Assert.True(SpinWait.SpinUntil(() => log.Count == 2, TimeSpan.FromSeconds(5)), string.Join("; ", log));

        Assert.Equal(["state paused", "counts paused 4"], log);
    }
}
