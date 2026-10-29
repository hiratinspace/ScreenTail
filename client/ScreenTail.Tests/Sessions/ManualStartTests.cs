using ScreenTail.Core.Ipc;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// A start over the pipe, whose only sender is the onboarding wizard's test session, under the same two
/// rules as the sessions the service starts itself, automatically or from its Ctrl+Alt+R hotkey: something
/// on screen says capture is on (INV-4), and the session starts under the tenant's policy as it stands now
/// (ST-047, INV-8), recorded on the session as for any other start.
/// </summary>
public sealed class ManualStartTests
{
    [Fact]
    public async Task AStartUnderALocalOnlyPolicyIsALocalOnlySessionWithThatPolicysVersion()
    {
        await using var harness = await MachineHarness.StartAsync();
        var start = new ManualStart(harness.Machine, () => true, () => (true, "v7"));

        var result = await start.StartAsync(new StartCommand { RequestId = 3 }, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(3, result.RequestId);
        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, TestContext.Current.CancellationToken))!;
        Assert.True(stored.LocalOnly);
        Assert.Equal("v7", stored.PolicyVersion);
    }

    [Fact]
    public async Task AStartWithLocalOnlyOffCarriesThePolicysVersionAndNothingMore()
    {
        // The control: the flag is the policy's, not always on.
        await using var harness = await MachineHarness.StartAsync();
        var start = new ManualStart(harness.Machine, () => true, () => (false, "v8"));

        Assert.True((await start.StartAsync(new StartCommand { RequestId = 1 }, TestContext.Current.CancellationToken)).Ok);

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!, TestContext.Current.CancellationToken))!;
        Assert.False(stored.LocalOnly);
        Assert.Equal("v8", stored.PolicyVersion);
    }

    [Fact]
    public async Task WithNothingOnScreenNothingStartsAndTheTechnicianIsToldWhyAndWhatToDo()
    {
        // The onboarding wizard shows this sentence under its Start button, so it has to say what is
        // missing and what to do about it, not only that it failed.
        await using var harness = await MachineHarness.StartAsync();
        var asked = false;
        var start = new ManualStart(harness.Machine, () => false, () =>
        {
            asked = true;
            return (true, "v7");
        });

        var result = await start.StartAsync(new StartCommand { RequestId = 2 }, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(SessionState.Idle, harness.Machine.State);
        Assert.False(asked);
        Assert.Equal(
            "The recording pill is not on screen, so the session was not started. ScreenTail never records without "
                + "showing it. Wait a few seconds for the pill to appear, then try again; if it does not, quit "
                + "ScreenTail from the tray and open it again.",
            result.Error);
    }

    [Fact]
    public async Task APillPutAwayBetweenSessionsStillLetsTheWizardStart()
    {
        // The pipe's start reads the reports as the service wires it, and a zero-size report, the pill put
        // away between sessions, counts for a start (owner decision, 2026-10-25). It stops counting once
        // the session runs, which is the indicator guard's business, not the start's.
        await using var harness = await MachineHarness.StartAsync();
        var reports = new IndicatorReports();
        reports.Report(Guid.NewGuid(), 0, 0);
        var start = new ManualStart(harness.Machine, () => reports.Showing, () => (false, "v8"));

        var result = await start.StartAsync(new StartCommand { RequestId = 4 }, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(SessionState.Recording, harness.Machine.State);
    }

    [Fact]
    public async Task AStartWhileRecordingIsRefusedWithTheState()
    {
        await using var harness = await MachineHarness.StartAsync();
        var start = new ManualStart(harness.Machine, () => true, () => (false, "v8"));
        Assert.True((await start.StartAsync(new StartCommand { RequestId = 1 }, TestContext.Current.CancellationToken)).Ok);

        var again = await start.StartAsync(new StartCommand { RequestId = 2 }, TestContext.Current.CancellationToken);

        Assert.False(again.Ok);
        Assert.Equal("Not possible while recording.", again.Error);
    }
}
