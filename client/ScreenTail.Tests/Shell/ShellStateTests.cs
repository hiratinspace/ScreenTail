using System.Diagnostics;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-070's state store: the one thing every view in the UI reads from.
///
/// The failure this exists to prevent is two windows disagreeing. A technician who sees "recording" in
/// Review and "idle" in the tray has no way to know which is true, and the answer they act on decides
/// whether a session gets recorded.
/// </summary>
public sealed class ShellStateTests
{
    [Fact]
    public void EveryViewSeesTheSameChangeAtOnce()
    {
        // ST-070's first criterion. Three views, one event, and none of them going to the pipe themselves.
        var state = new ShellState();
        var review = new List<ShellSnapshot>();
        var history = new List<ShellSnapshot>();
        var tray = new List<ShellSnapshot>();
        state.Changed += review.Add;
        state.Changed += history.Add;
        state.Changed += tray.Add;

        var clock = Stopwatch.StartNew();
        state.Observe(Recording("s1"));
        clock.Stop();

        Assert.Equal("recording", Assert.Single(review).Capture?.State);
        Assert.Equal("recording", Assert.Single(history).Capture?.State);
        Assert.Equal("recording", Assert.Single(tray).Capture?.State);
        Assert.True(clock.ElapsedMilliseconds < 100, $"fanning the change out took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TheServiceGoingAwayShowsTheBanner()
    {
        // ST-070's second criterion. A UI that quietly keeps showing the last state is how a technician
        // finds out at the end of a call that nothing was recorded.
        var state = new ShellState();
        state.Connected(Recording("s1"));

        state.Lost();

        Assert.Equal(ServiceConnection.Unavailable, state.Snapshot.Connection);
        Assert.Equal("Capture service not running — Start", state.Snapshot.Banner);
    }

    [Fact]
    public void NothingWrongMeansNoBanner()
    {
        // A bar that is always there is a bar nobody reads, including on the day it matters.
        var state = new ShellState();

        state.Connected(Recording("s1"));

        Assert.Null(state.Snapshot.Banner);
    }

    [Fact]
    public void ConnectingIsNotAnError()
    {
        // A service that is still starting is the normal case at login. Showing "not running" for the
        // first second would teach the technician to ignore the banner.
        var state = new ShellState();

        Assert.Equal(ServiceConnection.Connecting, state.Snapshot.Connection);
        Assert.Null(state.Snapshot.Banner);
    }

    [Fact]
    public void LosingThePipeDoesNotChangeWhatTheSessionWasDoing()
    {
        // The session did not stop because the UI lost its connection. Resetting to idle here would be the
        // UI telling a lie about somebody's recording — and the recording that is still running.
        var state = new ShellState();
        state.Connected(Recording("s1"));

        state.Lost();

        Assert.Equal("recording", state.Capture?.State);
        Assert.Equal("s1", state.Capture?.SessionId);
    }

    [Fact]
    public void AnEventArrivingProvesTheServiceIsBack()
    {
        // Evidence beats a retry timer. If the service is talking to us, we are connected, whatever the
        // UI last concluded.
        var state = new ShellState();
        state.Lost();

        state.Observe(Recording("s2"));

        Assert.Equal(ServiceConnection.Connected, state.Snapshot.Connection);
        Assert.Null(state.Snapshot.Banner);
    }

    [Fact]
    public void NavigationIsPartOfTheSameSnapshot()
    {
        // The shell chrome and the views read one object. Two sources of truth for "which view is open"
        // is how a back button and a navigation rail end up disagreeing.
        var state = new ShellState();
        var seen = new List<ShellSnapshot>();
        state.Changed += seen.Add;

        state.Navigate(ShellView.History);

        Assert.Equal(ShellView.History, Assert.Single(seen).View);
        Assert.Equal(ShellView.History, state.Snapshot.View);
    }

    [Fact]
    public void AHandlerThatReadsTheStateDoesNotDeadlock()
    {
        // Views do this constantly: a change arrives, and the handler asks the store something. Raising
        // the event while holding a non-reentrant lock would hang, and only under a race.
        var state = new ShellState();
        ShellSnapshot? readBack = null;
        state.Changed += _ => readBack = state.Snapshot;

        state.Observe(Recording("s1"));

        Assert.Equal("recording", readBack?.Capture?.State);
    }

    [Fact]
    public void AHandlerThatNavigatesDoesNotDeadlock()
    {
        // The real version of the same thing: "the session ended, show me Review".
        var state = new ShellState();
        var navigated = false;
        state.Changed += snapshot =>
        {
            if (snapshot.Capture?.State == "draft_ready" && !navigated)
            {
                navigated = true;
                state.Navigate(ShellView.Review);
            }
        };

        state.Observe(new CaptureStateSnapshot { State = "draft_ready", DraftsReady = 1 });

        Assert.True(navigated);
        Assert.Equal(ShellView.Review, state.Snapshot.View);
    }

    [Fact]
    public void WhatTheServiceLastSaidIsNotWhatIsTrueOnceItStopsAnswering()
    {
        // The store keeps the last capture state across a dropped pipe on purpose, so the UI can say
        // "it was recording, and may still be". The mistake was letting callers read that value as the
        // present tense. Idle, then a lost pipe, then a session the service starts by itself: the pill
        // read "Not recording" and stayed hidden through all of it (INV-4; 2026-09-19 review).
        //
        // KnownCapture is the present tense. It is null whenever nobody is answering.
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });

        Assert.NotNull(state.Snapshot.KnownCapture);

        state.Lost();

        Assert.NotNull(state.Snapshot.Capture);
        Assert.Null(state.Snapshot.KnownCapture);
    }

    [Fact]
    public void AHiddenPillComesBackTheMomentTheServiceStopsAnswering()
    {
        // The two halves together, which is the behaviour a technician would actually see.
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        Assert.False(ScreenTail.Core.Hud.HudState.For(state.Snapshot.KnownCapture, hidden: true).Visible);

        state.Lost();

        var hud = ScreenTail.Core.Hud.HudState.For(state.Snapshot.KnownCapture, hidden: true);
        Assert.True(hud.Visible);
        Assert.Contains("may still be recording", hud.State.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhileStillConnectingNothingIsKnownEither()
    {
        var state = new ShellState();
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Idle });
        state.Connecting();

        Assert.Null(state.Snapshot.KnownCapture);
    }

    private static CaptureStateSnapshot Recording(string sessionId) => new()
    {
        State = "recording",
        SessionId = sessionId,
        RemoteTool = "rdp",
        ElapsedMs = 12_000,
    };
}
