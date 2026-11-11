using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// History's "Mark as published" from the UI's side (ST-093, contract v10): the technician is asked
/// first, because it is a statement about the ticket; nothing is asked while a session is being
/// captured (Spec §4); and the service's answer is the result, in its own words when it refuses.
/// The service's rule is PublishCommandsTests.
/// </summary>
public sealed class MarkAsPublishedTests
{
    private readonly ShellState _state = new();
    private readonly List<IpcCommand> _sent = [];
    private CommandResult _answer = new() { RequestId = 1, Ok = true };

    [Fact]
    public async Task AgreeingSendsTheSessionByNameAndNothingElse()
    {
        _state.Connected(CaptureStateSnapshot.Idle);
        var asked = 0;

        var result = await Flow().RunAsync("s1", () => { asked++; return Task.FromResult(true); }, TestContext.Current.CancellationToken);

        Assert.True(result.Done, result.Error);
        Assert.Equal(1, asked);
        var mark = Assert.IsType<MarkPublishedCommand>(Assert.Single(_sent));
        Assert.Equal("s1", mark.SessionId);
    }

    [Fact]
    public async Task NotAgreeingSendsNothing()
    {
        _state.Connected(CaptureStateSnapshot.Idle);

        var result = await Flow().RunAsync("s1", () => Task.FromResult(false), TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedOutcome.Declined, result.Outcome);
        Assert.Null(result.Error);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    public async Task NothingIsAskedWhileASessionIsBeingCaptured(string state)
    {
        // Spec §4: no modal during a recording. The reason is said beside the button instead.
        _state.Connected(new CaptureStateSnapshot { State = state, SessionId = "live" });
        var asked = 0;

        var result = await Flow().RunAsync("s1", () => { asked++; return Task.FromResult(true); }, TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.Equal(MarkAsPublished.WhileCapturing, result.Error);
        Assert.Equal(0, asked);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftFailed)]
    [InlineData(CaptureStates.DraftPending)]
    public void BetweenSessionsTheTechnicianMayBeAsked(string state)
    {
        _state.Connected(new CaptureStateSnapshot { State = state, SessionId = state == CaptureStates.Idle ? null : "s9" });

        Assert.Null(Flow().BlockedBecause());
    }

    [Fact]
    public async Task WithNoServiceNothingIsAskedOrSent()
    {
        // The last thing the service said may be that it was recording, and it cannot be asked.
        _state.Connected(CaptureStateSnapshot.Idle);
        _state.Lost();
        var asked = 0;

        var result = await Flow().RunAsync("s1", () => { asked++; return Task.FromResult(true); }, TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedOutcome.NotAvailable, result.Outcome);
        Assert.Equal(ConfirmedAction.NotConnected, result.Error);
        Assert.Equal(0, asked);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ARefusalComesBackInTheServicesWords()
    {
        _state.Connected(CaptureStateSnapshot.Idle);
        _answer = new CommandResult { RequestId = 1, Ok = false, Error = Core.Review.Publish.PublishCommands.NothingToConfirm };

        var result = await Flow().RunAsync("s1", () => Task.FromResult(true), TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.Equal(Core.Review.Publish.PublishCommands.NothingToConfirm, result.Error);
    }

    [Fact]
    public async Task ARefusalWithoutWordsStillSaysNothingWasChanged()
    {
        _state.Connected(CaptureStateSnapshot.Idle);
        _answer = new CommandResult { RequestId = 1, Ok = false };

        var result = await Flow().RunAsync("s1", () => Task.FromResult(true), TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedAction.RefusedWithoutReason, result.Error);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Finalizing)]
    public async Task ACaptureThatStartedWhileTheDialogWasUpMeansNothingIsSent(string state)
    {
        // Review of #274: the service starts sessions by itself, and the capture check ran only before
        // the dialog. An answer given over a session that began meanwhile is not acted on; the reason is
        // said beside the button, and the technician can ask again once it has stopped.
        _state.Connected(CaptureStateSnapshot.Idle);

        var result = await Flow().RunAsync(
            "s1",
            () =>
            {
                _state.Observe(new CaptureStateSnapshot { State = state, SessionId = "live" });
                return Task.FromResult(true);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.Equal(MarkAsPublished.WhileCapturing, result.Error);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AServiceLostWhileTheDialogWasUpMeansNothingIsSent()
    {
        _state.Connected(CaptureStateSnapshot.Idle);

        var result = await Flow().RunAsync(
            "s1",
            () =>
            {
                _state.Lost();
                return Task.FromResult(true);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfirmedOutcome.NotAvailable, result.Outcome);
        Assert.Equal(ConfirmedAction.NotConnected, result.Error);
        Assert.Empty(_sent);
    }

    [Fact]
    public void TheQuestionNamesTheSessionAsHistoryShowsIt()
    {
        // Which row is being vouched for, in the words on the row: when it started, how long, the tool.
        var question = MarkAsPublished.QuestionFor("Tue 10 Nov 14:02, 9:00, screenconnect");

        Assert.StartsWith("Mark the session started Tue 10 Nov 14:02, 9:00, screenconnect as published?", question, StringComparison.Ordinal);
        Assert.EndsWith(MarkAsPublished.Question, question, StringComparison.Ordinal);
        Assert.DoesNotContain('—', question);
    }

    [Fact]
    public void TheWordsArePlainAndSayWhatIsBeingClaimed()
    {
        // The technician is vouching for the ticket, and is told what follows: nothing is sent again and
        // the session can no longer be discarded.
        Assert.Contains("checking the ticket", MarkAsPublished.Question, StringComparison.Ordinal);
        Assert.Contains("will not send it again", MarkAsPublished.Question, StringComparison.Ordinal);
        Assert.Contains("no longer be discarded", MarkAsPublished.Question, StringComparison.Ordinal);
        Assert.All(
            new[] { MarkAsPublished.Action, MarkAsPublished.Question, MarkAsPublished.WhileCapturing, MarkAsPublished.Done },
            text => Assert.DoesNotContain('\u2014', text));
    }

    private MarkAsPublished Flow() => new(
        _state,
        (build, _) =>
        {
            _sent.Add(build(1));
            return Task.FromResult(_answer);
        });
}
