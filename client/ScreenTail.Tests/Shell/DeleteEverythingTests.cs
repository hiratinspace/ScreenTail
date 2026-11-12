using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// Settings → Privacy's "Delete everything" from the UI's side (Spec §5 S6, INV-12). Its typed dialog is a
/// modal, and Spec §4 allows none while a session is being captured, so nothing is asked then and the
/// reason is said beside the button. The state is read again once the dialog is answered, as the mark
/// and History's bulk discard do: the service starts sessions by itself.
/// </summary>
public sealed class DeleteEverythingTests
{
    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    public async Task NothingIsAskedWhileASessionIsBeingCaptured(string capturing)
    {
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = capturing, SessionId = "live" });
        var (asked, shown, sent) = (0, 0, 0);
        var erase = Offline(state, () => asked++, () => sent++);

        var result = await erase.RunAsync(_ =>
        {
            shown++;
            return Task.FromResult(true);
        }, Ct);

        Assert.Equal((ConfirmedOutcome.Refused, DeleteEverything.WhileCapturing), (result.Outcome, result.Error));
        Assert.Equal(DeleteEverything.WhileCapturing, erase.BlockedBecause());
        Assert.Equal((0, 0, 0), (asked, shown, sent));
    }

    [Theory]
    [InlineData(CaptureStates.Idle)]
    [InlineData(CaptureStates.DraftReady)]
    [InlineData(CaptureStates.DraftPending)]
    public async Task WithNothingBeingCapturedTheTechnicianIsAskedAndTheEraseIsSent(string idle)
    {
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = idle });
        var asked = new List<string>();
        IpcCommand? sent = null;
        var erase = new DeleteEverything(
            state,
            (build, _) =>
            {
                var request = Assert.IsType<RequestConfirmationCommand>(build(1));
                Assert.Equal("erase_everything", request.Action);
                return Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t", Phrase = "DELETE EVERYTHING" });
            },
            (build, _) =>
            {
                sent = build(2);
                return Task.FromResult(new CommandResult { RequestId = 2, Ok = true });
            });

        var result = await erase.RunAsync(phrase =>
        {
            asked.Add(phrase);
            return Task.FromResult(true);
        }, Ct);

        Assert.True(result.Done, result.Error);
        Assert.Null(erase.BlockedBecause());
        Assert.Equal(["DELETE EVERYTHING"], asked);
        Assert.Equal("t", Assert.IsType<EraseAllLocalDataCommand>(sent).Confirmation);
    }

    [Fact]
    public async Task ASessionThatBeginsWhileThePhraseIsTypedStopsTheErase()
    {
        var state = new ShellState();
        state.Connected(CaptureStateSnapshot.Idle);
        var sent = 0;
        var erase = Offline(state, () => { }, () => sent++);

        var result = await erase.RunAsync(_ =>
        {
            state.Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "new" });
            return Task.FromResult(true);
        }, Ct);

        Assert.Equal((ConfirmedOutcome.Refused, DeleteEverything.WhileCapturing), (result.Outcome, result.Error));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task AServiceThatGoesWhileThePhraseIsTypedIsSentNothing()
    {
        var state = new ShellState();
        state.Connected(CaptureStateSnapshot.Idle);
        var sent = 0;
        var erase = Offline(state, () => { }, () => sent++);

        var result = await erase.RunAsync(_ =>
        {
            state.Lost();
            return Task.FromResult(true);
        }, Ct);

        Assert.Equal((ConfirmedOutcome.NotAvailable, ConfirmedAction.NotConnected), (result.Outcome, result.Error));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task WithNoServiceNothingIsShownAndNothingIsSent()
    {
        // KnownCapture is null when the service is not answering, which may mean it is still recording.
        var state = new ShellState();
        state.Connected(CaptureStateSnapshot.Idle);
        state.Lost();
        var (asked, shown, sent) = (0, 0, 0);
        var erase = Offline(state, () => asked++, () => sent++);

        var result = await erase.RunAsync(_ =>
        {
            shown++;
            return Task.FromResult(true);
        }, Ct);

        Assert.Equal((ConfirmedOutcome.NotAvailable, ConfirmedAction.NotConnected), (result.Outcome, result.Error));
        Assert.Equal((0, 0, 0), (asked, shown, sent));
    }

    [Fact]
    public async Task NotTypingThePhraseIsADecline()
    {
        var state = new ShellState();
        state.Connected(CaptureStateSnapshot.Idle);
        var sent = 0;
        var erase = Offline(state, () => { }, () => sent++);

        var result = await erase.RunAsync(_ => Task.FromResult(false), Ct);

        Assert.Equal(ConfirmedOutcome.Declined, result.Outcome);
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task ARefusalComesBackInTheServicesWords()
    {
        var state = new ShellState();
        state.Connected(CaptureStateSnapshot.Idle);
        var erase = new DeleteEverything(
            state,
            (_, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t", Phrase = "DELETE EVERYTHING" }),
            (_, _) => Task.FromResult(new CommandResult { RequestId = 1, Ok = false, Error = "The token has expired." }));

        var result = await erase.RunAsync(_ => Task.FromResult(true), Ct);

        Assert.Equal((ConfirmedOutcome.Refused, "The token has expired."), (result.Outcome, result.Error));
    }

    private static DeleteEverything Offline(ShellState state, Action asked, Action sent) => new(
        state,
        (_, _) =>
        {
            asked();
            return Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t", Phrase = "DELETE EVERYTHING" });
        },
        (_, _) =>
        {
            sent();
            return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
