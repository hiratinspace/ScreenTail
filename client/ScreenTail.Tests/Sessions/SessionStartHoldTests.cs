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

    [Theory]
    [InlineData(SessionState.DraftReady)]
    [InlineData(SessionState.DraftFailed)]
    [InlineData(SessionState.DraftPending)]
    public async Task AFinishedSessionWaitingForReviewDoesNotStopAHold(SessionState resting)
    {
        // The machine rests after a session in draft_ready, draft_failed or (since #230) draft_pending,
        // its draft on its way to the backend. Nothing is being recorded in any of them, so an update may
        // hold starts there, and while it does no session starts from there either.
        await using var harness = await MachineHarness.StartAsync(drafter: new Drafter(resting));
        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: TestContext.Current.CancellationToken));
        Assert.True(await harness.Machine.StopAsync(TestContext.Current.CancellationToken));
        Assert.Equal(resting, harness.Machine.State);

        using var hold = await harness.Machine.HoldStartsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(hold);
        Assert.False(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, ct: TestContext.Current.CancellationToken));
        Assert.Equal(resting, harness.Machine.State);
    }

    /// <summary>Drafts so that the session comes to rest in the state asked for.</summary>
    private sealed class Drafter(SessionState resting) : IDrafter
    {
        public Task<DraftOutcome> DraftAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(resting switch
        {
            SessionState.DraftReady => DraftOutcome.Success(new DraftNote
            {
                Problem = "p",
                Steps = [],
                Result = "r",
                FollowUps = [],
                SuggestedTitle = "t",
                SuggestedTimeMinutes = 15,
                KbCandidate = false,
                KbReason = "n",
                Source = DraftSource.Cloud,
                PromptVersion = "note_v1",
            }),
            SessionState.DraftPending => DraftOutcome.Pending("The draft is queued for the backend."),
            _ => DraftOutcome.Failure("No drafting here."),
        });
    }
}
