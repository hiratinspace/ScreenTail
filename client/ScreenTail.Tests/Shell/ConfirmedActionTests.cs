using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// The typed-confirmation flow behind Discard and Delete everything (ST-085, Spec §3; 2026-10-12 review
/// S-46, S-50, T-158). The service's answer to the destructive command is the thing that matters, and it
/// used to be dropped: a refused erase closed its dialog exactly as a successful one did.
/// </summary>
public sealed class ConfirmedActionTests
{
    [Fact]
    public async Task ARefusedEraseIsReportedRatherThanLookingDone()
    {
        // A session running, or a token older than thirty seconds: the service says no, in words.
        var result = await ConfirmedAction.RunAsync(
            issue: (_, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t1", Phrase = "DELETE" }),
            action: "erase_everything",
            typed: _ => Task.FromResult(true),
            build: token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            send: (_, _) => Task.FromResult(new CommandResult { RequestId = 1, Ok = false, Error = "Stop the session first." }));

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.False(result.Done);
        Assert.Equal("Stop the session first.", result.Error);
    }

    [Fact]
    public async Task ARefusalWithNoWordsStillSaysNothingChanged()
    {
        var result = await ConfirmedAction.RunAsync(
            (_, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t1", Phrase = "DISCARD" }),
            "discard_session",
            _ => Task.FromResult(true),
            token => id => new DiscardCommand { RequestId = id, Confirmation = token },
            (_, _) => Task.FromResult(new CommandResult { RequestId = 1, Ok = false }));

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.Equal(ConfirmedAction.RefusedWithoutReason, result.Error);
    }

    [Fact]
    public async Task TheTokenTheServiceIssuedTravelsBackOnTheCommand()
    {
        IpcCommand? sent = null;
        var issued = 0;
        var result = await ConfirmedAction.RunAsync(
            (action, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = $"issued-for-{action}-{++issued}", Phrase = "DELETE" }),
            "erase_everything",
            phrase => Task.FromResult(phrase == "DELETE"),
            token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            (build, _) =>
            {
                sent = build(7);
                return Task.FromResult(new CommandResult { RequestId = 7, Ok = true });
            });

        Assert.Equal(ConfirmedOutcome.Done, result.Outcome);
        Assert.True(result.Done);
        Assert.Null(result.Error);
        var erase = Assert.IsType<EraseAllLocalDataCommand>(sent);
        Assert.Equal("issued-for-erase_everything-2", erase.Confirmation);
        Assert.Equal(7, erase.RequestId);
    }

    [Fact]
    public async Task DecliningSendsNothing()
    {
        var sends = 0;
        var result = await ConfirmedAction.RunAsync(
            (_, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t1", Phrase = "DISCARD" }),
            "discard_session",
            _ => Task.FromResult(false),
            token => id => new DiscardCommand { RequestId = id, Confirmation = token },
            (_, _) =>
            {
                sends++;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        Assert.Equal(ConfirmedOutcome.Declined, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task NoServiceMeansNoDialogAndNothingSent()
    {
        // A discard sent into a dropped pipe would be reported as done and would not be. Nothing is shown
        // either: a dialog that led nowhere is worse than none.
        var asked = 0;
        var sends = 0;
        var result = await ConfirmedAction.RunAsync(
            (_, _) => Task.FromResult<ConfirmationIssued?>(null),
            "erase_everything",
            _ =>
            {
                asked++;
                return Task.FromResult(true);
            },
            token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            (_, _) =>
            {
                sends++;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        Assert.Equal(ConfirmedOutcome.NotAvailable, result.Outcome);
        Assert.Equal(ConfirmedAction.NotConnected, result.Error);
        Assert.Equal(0, asked);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task CancellationReachesEveryRoundTrip()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        _ = await ConfirmedAction.RunAsync(
            (_, ct) =>
            {
                tokens.Add(ct);
                return Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t1", Phrase = "DELETE" });
            },
            "erase_everything",
            _ => Task.FromResult(true),
            token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            (_, ct) =>
            {
                tokens.Add(ct);
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            },
            cts.Token);

        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(cts.Token, t));
    }

    [Fact]
    public async Task TheTokenSpentIsIssuedAfterThePhraseIsTyped()
    {
        // The service gives a token thirty seconds. One issued before the dialog opened was spent on
        // reading and typing, so a careful technician's yes came back refused (S-46).
        var steps = new List<string>();
        var issued = 0;
        string? spent = null;
        _ = await ConfirmedAction.RunAsync(
            (_, _) =>
            {
                steps.Add("issue");
                return Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t" + ++issued, Phrase = "DELETE EVERYTHING" });
            },
            "erase_everything",
            _ =>
            {
                steps.Add("typed");
                return Task.FromResult(true);
            },
            token => id => new EraseAllLocalDataCommand { RequestId = id, Confirmation = token },
            (build, _) =>
            {
                steps.Add("send");
                spent = ((EraseAllLocalDataCommand)build(1)).Confirmation;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        Assert.Equal(["issue", "typed", "issue", "send"], steps);
        Assert.Equal("t2", spent);
    }

    [Fact]
    public async Task AServiceGoneWhileTheTechnicianTypedSendsNothing()
    {
        var asks = 0;
        var sends = 0;
        var result = await ConfirmedAction.RunAsync(
            (_, _) => Task.FromResult(++asks == 1 ? new ConfirmationIssued { Token = "t1", Phrase = "DISCARD" } : null),
            "discard_session",
            _ => Task.FromResult(true),
            token => id => new DiscardCommand { RequestId = id, Confirmation = token },
            (_, _) =>
            {
                sends++;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        Assert.Equal(ConfirmedOutcome.NotAvailable, result.Outcome);
        Assert.Equal(ConfirmedAction.NotConnected, result.Error);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task WordsThatChangedWhileTheTechnicianTypedSendNothing()
    {
        // Agreeing to one sentence is not agreeing to another: a service that now asks for different
        // words gets nothing until the technician has read them.
        var asks = 0;
        var sends = 0;
        var result = await ConfirmedAction.RunAsync(
            (_, _) => Task.FromResult<ConfirmationIssued?>(new ConfirmationIssued { Token = "t" + ++asks, Phrase = asks == 1 ? "DISCARD" : "DISCARD ALL" }),
            "discard_session",
            _ => Task.FromResult(true),
            token => id => new DiscardCommand { RequestId = id, Confirmation = token },
            (_, _) =>
            {
                sends++;
                return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
            });

        Assert.Equal(ConfirmedOutcome.Refused, result.Outcome);
        Assert.Equal(ConfirmedAction.PhraseChanged, result.Error);
        Assert.Equal(0, sends);
    }
}
