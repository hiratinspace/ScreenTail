using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// Which pane the shell shows, and when it asks the service for anything (ST-070; 2026-10-12 review
/// S-144, T-161, T-159, T-162). Platform-neutral, so the decisions the shell window only carries out are
/// held here on any machine.
/// </summary>
public sealed class ShellPanesTests
{
    [Fact]
    public void ACaptureStateChangeDoesNotReloadSettings()
    {
        // Every state change while Settings was open read all four panels again, which overwrote the
        // technician's unsaved local-only and excluded-app edits, and Save then stored the old values.
        var state = new ShellState();
        var panes = new ShellPanes();
        var steps = new List<PaneStep>();
        state.Changed += snapshot => steps.Add(panes.Next(snapshot));

        state.Connected(Capture(CaptureStates.Idle));
        state.Navigate(ShellView.Settings);
        state.Observe(Capture(CaptureStates.Recording, "s1", elapsed: 1_000));
        state.Observe(Capture(CaptureStates.Recording, "s1", elapsed: 2_000));
        state.Lost();

        var settings = steps.Where(s => s.Action == PaneAction.Settings).ToList();
        Assert.Equal(4, settings.Count);
        Assert.Single(settings, s => s.LoadSettings);
        Assert.True(settings[0].LoadSettings, "the panels were not read when the view became Settings");
        Assert.False(settings[0].KeepEdits, "a view entered afresh is read whole");
    }

    [Fact]
    public void TheServiceComingBackRereadsSettingsKeepingUnsavedEdits()
    {
        // Settings opened while the service was away says it did not answer, and stayed saying so after it
        // came back, because only entering the view read the panels. A reconnect now reads them again, but
        // only those with nothing unsaved: the technician's edits are never overwritten.
        var state = new ShellState();
        var panes = new ShellPanes();
        var steps = new List<PaneStep>();
        state.Changed += snapshot => steps.Add(panes.Next(snapshot));

        state.Connecting();
        state.Navigate(ShellView.Settings);
        state.Connected(Capture(CaptureStates.Idle));
        state.Observe(Capture(CaptureStates.Recording, "s1", elapsed: 1_000));
        state.Lost();

        // An event while the shell thought the service was away is the service back, as the store says.
        state.Observe(Capture(CaptureStates.Recording, "s1", elapsed: 2_000));
        state.Connected(Capture(CaptureStates.Recording, "s1", elapsed: 3_000));
        state.Lost();
        state.Connected(Capture(CaptureStates.Recording, "s1", elapsed: 4_000));
        state.Observe(Capture(CaptureStates.Recording, "s1", elapsed: 5_000));

        Assert.Equal(
            [(true, false), (true, true), (false, false), (false, false), (true, true), (false, false), (false, false), (true, true), (false, false)],
            steps.Where(s => s.Action == PaneAction.Settings).Select(s => (s.LoadSettings, s.KeepEdits)));
    }

    [Fact]
    public void TheServiceComingBackWhileAnotherViewIsOpenReadsNoSettings()
    {
        var panes = new ShellPanes();
        Assert.Equal(PaneAction.History, panes.Next(Snapshot(ShellView.History) with { Connection = ServiceConnection.Unavailable }).Action);
        Assert.Equal(PaneAction.History, panes.Next(Snapshot(ShellView.History)).Action);

        // Entering Settings afterwards reads it whole, as any entry does.
        Assert.Equal(new PaneStep(PaneAction.Settings, LoadSettings: true), panes.Next(Snapshot(ShellView.Settings)));
    }

    [Fact]
    public void ComingBackToSettingsReadsItAgain()
    {
        // The control: a view entered afresh is read, so a policy change made meanwhile is shown.
        var panes = new ShellPanes();

        Assert.True(panes.Next(Snapshot(ShellView.Settings)).LoadSettings);
        Assert.False(panes.Next(Snapshot(ShellView.Settings)).LoadSettings);
        Assert.Equal(PaneAction.History, panes.Next(Snapshot(ShellView.History)).Action);
        Assert.True(panes.Next(Snapshot(ShellView.Settings)).LoadSettings);
    }

    [Fact]
    public void ReturningToReviewShowsTheSessionAgain()
    {
        // Review for s1, then History, then s1 opened again from History: only a different session used
        // to count as a change, so the History pane stayed on screen under the Review heading.
        var state = new ShellState();
        var panes = new ShellPanes();
        state.Connected(Capture(CaptureStates.DraftReady, "s1"));

        Assert.Equal(new PaneStep(PaneAction.LoadReview, "s1"), panes.Next(state.Snapshot));
        state.Navigate(ShellView.History);
        Assert.Equal(PaneAction.History, panes.Next(state.Snapshot).Action);
        state.OpenSession("s1");

        Assert.Equal(new PaneStep(PaneAction.ShowReview, "s1"), panes.Next(state.Snapshot));
        Assert.Equal(PaneAction.None, panes.Next(state.Snapshot).Action);
    }

    [Fact]
    public void ASecondSnapshotForTheSameSessionDoesNotAskTwice()
    {
        var panes = new ShellPanes();
        var snapshot = Snapshot(ShellView.Review, Capture(CaptureStates.DraftReady, "s1"), reviewing: "s1");

        Assert.Equal(PaneAction.LoadReview, panes.Next(snapshot).Action);
        Assert.Equal(PaneAction.None, panes.Next(snapshot).Action);
        Assert.Equal(PaneAction.None, panes.Next(snapshot with { Connection = ServiceConnection.Unavailable }).Action);
    }

    [Fact]
    public void ADifferentSessionIsLoaded()
    {
        var panes = new ShellPanes();
        _ = panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.DraftReady, "s1"), reviewing: "s1"));

        Assert.Equal(new PaneStep(PaneAction.LoadReview, "s2"), panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.DraftReady, "s2"), reviewing: "s2")));
    }

    [Fact]
    public void ASessionOpenedWhileRecordingIsLoadedAgainWhenItsDraftArrives()
    {
        // The pill's double-click opens Review of the session being recorded. Its draft arriving later
        // names the same session, which must not leave the note-less pane on screen.
        var panes = new ShellPanes();
        Assert.Equal(PaneAction.LoadReview, panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.Recording, "s1"), reviewing: "s1")).Action);
        Assert.Equal(PaneAction.None, panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.Finalizing, "s1"), reviewing: "s1")).Action);

        Assert.Equal(new PaneStep(PaneAction.LoadReview, "s1"), panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.DraftReady, "s1"), reviewing: "s1")));

        // Once. The service repeats its state on every reconnect.
        Assert.Equal(PaneAction.None, panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.DraftReady, "s1"), reviewing: "s1")).Action);
        Assert.Equal(PaneAction.None, panes.Next(Snapshot(ShellView.Review, Capture(CaptureStates.Recording, "s2"), reviewing: "s1")).Action);
    }

    [Fact]
    public void ASessionTheServiceWouldNotHandOverIsAskedForAgainOnlyWhenTheTechnicianComesBack()
    {
        var panes = new ShellPanes();
        var reviewing = Snapshot(ShellView.Review, Capture(CaptureStates.Recording, "s9", elapsed: 1_000), reviewing: "s1");
        Assert.Equal(PaneAction.LoadReview, panes.Next(reviewing).Action);
        panes.Failed("s1");

        // State changes while the failure is on screen ask nothing.
        Assert.Equal(PaneAction.None, panes.Next(reviewing with { Capture = Capture(CaptureStates.Recording, "s9", elapsed: 2_000) }).Action);

        _ = panes.Next(Snapshot(ShellView.History, reviewing: "s1"));
        Assert.Equal(new PaneStep(PaneAction.LoadReview, "s1"), panes.Next(reviewing));
    }

    [Fact]
    public void AFailureForASessionNoLongerShownChangesNothing()
    {
        var panes = new ShellPanes();
        _ = panes.Next(Snapshot(ShellView.Review, reviewing: "s1"));
        _ = panes.Next(Snapshot(ShellView.Review, reviewing: "s2"));
        panes.Failed("s1");
        _ = panes.Next(Snapshot(ShellView.History, reviewing: "s2"));

        Assert.Equal(new PaneStep(PaneAction.ShowReview, "s2"), panes.Next(Snapshot(ShellView.Review, reviewing: "s2")));
    }

    [Fact]
    public void NothingToReviewForgetsTheLastSession()
    {
        var panes = new ShellPanes();
        _ = panes.Next(Snapshot(ShellView.Review, reviewing: "s1"));

        Assert.Equal(PaneAction.NothingToReview, panes.Next(Snapshot(ShellView.Review)).Action);
        Assert.Null(panes.Review);
        Assert.Equal(PaneAction.LoadReview, panes.Next(Snapshot(ShellView.Review, reviewing: "s1")).Action);
    }

    [Theory]
    [InlineData("recording_with_audio")]
    [InlineData("")]
    [InlineData("IDLE")]
    public void TheShellNeverCallsAStateItDoesNotKnowIdle(string state)
    {
        // The tray and the pill call an unrecognised state unknown. The shell called it "Idle", so a newer
        // service's recording-like state read as nothing happening in one of the three (T-162).
        var status = ShellStatus.Describe(Snapshot(ShellView.Review, Capture(state)));

        Assert.Equal(ShellStatus.Unknown, status);
        Assert.DoesNotContain("Idle", status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CaptureStates.Idle, "Idle")]
    [InlineData(CaptureStates.Recording, "Recording")]
    [InlineData(CaptureStates.Paused, "Paused")]
    [InlineData(CaptureStates.Suppressed, "Paused: sensitive window")]
    [InlineData(CaptureStates.Finalizing, "Finishing up")]
    [InlineData(CaptureStates.DraftReady, "Draft ready")]
    [InlineData(CaptureStates.DraftFailed, "Draft failed")]
    public void EveryStateTheServiceSendsKeepsItsWords(string state, string expected)
    {
        Assert.Equal(expected, ShellStatus.Describe(Snapshot(ShellView.Review, Capture(state))));
    }

    [Fact]
    public void TheStatusLineSaysTheToolAndHowLong()
    {
        var capture = Capture(CaptureStates.Recording, "s1", elapsed: 12 * 60 * 1000 + 41_000) with { RemoteTool = "screenconnect" };

        Assert.Equal("Recording: screenconnect (12:41)", ShellStatus.Describe(Snapshot(ShellView.Review, capture)));
        Assert.Equal(ShellStatus.Unknown + " (12:41)", ShellStatus.Describe(Snapshot(ShellView.Review, capture with { State = "new_state", RemoteTool = null })));
    }

    [Fact]
    public void NotConnectedAndConnectingAreSaidAsSuch()
    {
        Assert.Equal(ShellStatus.Connecting, ShellStatus.Describe(new ShellState().Snapshot));
        Assert.Equal(ShellStatus.NotConnected, ShellStatus.Describe(Snapshot(ShellView.Review, Capture(CaptureStates.Recording)) with { Connection = ServiceConnection.Unavailable }));
    }

    private static CaptureStateSnapshot Capture(string state, string? session = null, long? elapsed = null) =>
        new() { State = state, SessionId = session, ElapsedMs = elapsed };

    private static ShellSnapshot Snapshot(ShellView view, CaptureStateSnapshot? capture = null, string? reviewing = null) =>
        new(ServiceConnection.Connected, capture ?? Capture(CaptureStates.Idle), view, Banner: null, reviewing);
}
