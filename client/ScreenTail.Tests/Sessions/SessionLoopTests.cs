using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// The loop the click drain, the scene sampler and the sensitive-context guard run on (ST-031): a timer
/// only while a session is under way, a turn at once when one begins, and one more after it ends.
/// </summary>
public sealed class SessionLoopTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Fact]
    public async Task WithNoSessionThereIsNoTimerAndNoTurn()
    {
        var harness = await HarnessAsync();
        var time = new CountingTime();
        var turns = 0;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = SessionLoop.RunAsync(harness.Machine, TimeSpan.FromMilliseconds(10), _ => { Interlocked.Increment(ref turns); return Task.CompletedTask; }, null, stopping.Token, time);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await stopping.CancelAsync();
        await running;

        Assert.Equal(0, time.Created);
        Assert.Equal(0, Volatile.Read(ref turns));
    }

    [Fact]
    public async Task ASessionStartingIsATurnAtOnceWithoutWaitingForTheTimer()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var time = new CountingTime(frozen: true);
        var seen = new List<SessionState>();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = SessionLoop.RunAsync(harness.Machine, TimeSpan.FromMilliseconds(10), _ => Record(seen, harness.Machine.State), null, stopping.Token, time);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));

        var turned = await UntilAsync(() => Count(seen) == 1);
        await stopping.CancelAsync();
        await running;

        Assert.True(turned, "the session's first turn waited for a timer");
        Assert.Equal(1, time.Created);
    }

    [Fact]
    public async Task ASessionEndingIsOneMoreTurnThatSeesItOverAndTheTimerGoes()
    {
        // The scene sampler forgets its baseline on seeing a session over, and the drain empties what the
        // hooks wrote before they came out. Both depend on a turn after the end.
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var time = new CountingTime();
        var seen = new List<SessionState>();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = SessionLoop.RunAsync(harness.Machine, TimeSpan.FromMilliseconds(10), _ => Record(seen, harness.Machine.State), null, stopping.Token, time);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));
        Assert.True(await UntilAsync(() => Count(seen) >= 3), "the loop did not tick during the session");
        Assert.True(await harness.Machine.StopAsync(ct));

        var gone = await UntilAsync(() => time.Live == 0);
        var sawItOver = await UntilAsync(() => !SessionLoop.IsUnderWay(Last(seen)));
        var after = Count(seen);
        await Task.Delay(100, ct);
        await stopping.CancelAsync();
        await running;

        Assert.True(gone, "the timer outlived the session");
        Assert.True(sawItOver, $"the last turn saw {Last(seen)}, not a session that had ended");
        Assert.Equal(after, Count(seen));
    }

    [Fact]
    public async Task ASessionThatEndsDuringATurnStillGetsATurnThatSeesItOver()
    {
        // The case the extra turn exists for. The session ends while a turn is running, so that turn saw
        // it recording and the next look finds it over before any tick could come round. The clock is
        // frozen, so no timer can supply the turn: only the loop's own last turn can.
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var seen = new List<SessionState>();
        var stopped = 0;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = SessionLoop.RunAsync(
            harness.Machine,
            TimeSpan.FromMilliseconds(10),
            async token =>
            {
                await Record(seen, harness.Machine.State);
                if (Interlocked.Exchange(ref stopped, 1) == 0)
                {
                    Assert.True(await harness.Machine.StopAsync(token));
                }
            },
            null,
            stopping.Token,
            new CountingTime(frozen: true));
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));

        var sawItOver = await UntilAsync(() => Count(seen) >= 2);
        await stopping.CancelAsync();
        await running;

        Assert.True(sawItOver, $"after the turn in which the session ended, no turn saw it over (turns: {Count(seen)})");
        Assert.Equal(SessionState.Recording, seen[0]);
        Assert.False(SessionLoop.IsUnderWay(Last(seen)), $"the last turn saw {Last(seen)}");
    }

    [Fact]
    public async Task ATurnThatThrowsCostsThatTurnAndTheLoopGoesOn()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.Current.CancellationToken;
        var turns = 0;
        var failures = 0;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = SessionLoop.RunAsync(
            harness.Machine,
            TimeSpan.FromMilliseconds(10),
            _ => Interlocked.Increment(ref turns) == 1 ? throw new InvalidOperationException("one bad turn") : Task.CompletedTask,
            _ => Interlocked.Increment(ref failures),
            stopping.Token);
        Assert.True(await harness.Machine.StartAsync(Rdp, ct: ct));

        var carriedOn = await UntilAsync(() => Volatile.Read(ref turns) >= 3);
        await stopping.CancelAsync();
        await running;

        Assert.True(carriedOn);
        Assert.Equal(1, Volatile.Read(ref failures));
    }

    [Theory]
    [InlineData(SessionState.Idle, false)]
    [InlineData(SessionState.Recording, true)]
    [InlineData(SessionState.Paused, true)]
    [InlineData(SessionState.Suppressed, true)]
    [InlineData(SessionState.Finalizing, true)]
    [InlineData(SessionState.DraftReady, false)]
    [InlineData(SessionState.DraftFailed, false)]
    public void ASessionIsUnderWayFromRecordingUntilItHasFinalized(SessionState state, bool underWay) =>
        Assert.Equal(underWay, SessionLoop.IsUnderWay(state));

    private static Task Record(List<SessionState> seen, SessionState state)
    {
        lock (seen)
        {
            seen.Add(state);
        }

        return Task.CompletedTask;
    }

    private static SessionState Last(List<SessionState> seen)
    {
        lock (seen)
        {
            return seen[^1];
        }
    }

    private static int Count(List<SessionState> seen)
    {
        lock (seen)
        {
            return seen.Count;
        }
    }

    private async Task<MachineHarness> HarnessAsync() => _harness ??= await MachineHarness.StartAsync();

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
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

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
