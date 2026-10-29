using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// ST-113's "never during a session", held where sessions start: Windows Installer ends this process to
/// replace it, so an install may begin only when no session is under way, and no session may begin until
/// the install has finished. One step under the machine's transition lock, so the two cannot interleave.
/// </summary>
public sealed class SessionStartHoldTests
{
    [Fact]
    public async Task WhileASessionIsRecordingNoHoldIsGranted()
    {
        await using var harness = await MachineHarness.StartAsync();
        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: TestContext.Current.CancellationToken));

        Assert.Null(await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken));

        Assert.True(await harness.Machine.PauseAsync(TestContext.Current.CancellationToken));
        Assert.Null(await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhileHeldNoSessionStartsAndOnceReleasedOneDoes()
    {
        await using var harness = await MachineHarness.StartAsync();

        var hold = await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(hold);
        Assert.True(harness.Machine.StartsHeld);
        Assert.False(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: TestContext.Current.CancellationToken));
        Assert.Equal(SessionState.Idle, harness.Machine.State);
        Assert.Null(await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken));

        hold!.Dispose();
        hold.Dispose();

        Assert.False(harness.Machine.StartsHeld);
        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStartOverThePipeWhileHeldSaysWhy()
    {
        await using var harness = await MachineHarness.StartAsync();
        using var hold = await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken);
        var start = new ManualStart(harness.Machine, () => true, () => (false, "v8"));

        var result = await start.StartAsync(new StartCommand { RequestId = 4 }, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(ManualStart.Updating, result.Error);
        Assert.Equal(SessionState.Idle, harness.Machine.State);
    }

    [Fact]
    public async Task AFinishedSessionWaitingForReviewDoesNotStopAHold()
    {
        // The machine rests in draft_ready or draft_failed after a session; nothing is being recorded.
        await using var harness = await MachineHarness.StartAsync();
        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: TestContext.Current.CancellationToken));
        Assert.True(await harness.Machine.StopAsync(TestContext.Current.CancellationToken));
        Assert.True(harness.Machine.State is SessionState.DraftReady or SessionState.DraftFailed, harness.Machine.State.ToString());

        using var hold = await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(hold);
    }
}
