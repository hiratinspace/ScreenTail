using ScreenTrail.Core.Detection;
using ScreenTrail.Core.History;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Tests.History;

/// <summary>
/// ST-079's denominator (spec v0.7.3 S4): what counts as one remote session observed, and when one that was
/// not captured is kept, with which reason. On a hand-moved clock, so the grace and the minimum are run out
/// in microseconds.
/// </summary>
public sealed class RemoteVisitsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);

    private static readonly ScopeDecision AnyDesk = new(CaptureScope.RemoteTool, "anydesk", RemoteToolKind.Anydesk, "Capturing: AnyDesk", 1);
    private static readonly ScopeDecision ScreenConnect = new(CaptureScope.RemoteTool, "screenconnect", RemoteToolKind.Screenconnect, "Capturing: ScreenConnect", 2);
    private static readonly ScopeDecision Outlook = new(CaptureScope.OutOfScope, null, null, "Not capturing: OUTLOOK");
    private static readonly ScopeDecision PowerShell = new(CaptureScope.AdminTool, "powershell", null, "Capturing: PowerShell", 3);
    private static readonly ScopeDecision Splashtop = new(CaptureScope.OutOfScope, "splashtop", RemoteToolKind.Splashtop, "Not capturing: Splashtop is switched off in Settings.");

    private readonly ManualTime _time = new(T0);

    private RemoteVisits Visits() => new(() => Grace, TimeSpan.FromSeconds(60), _time);

    [Fact]
    public void OneLongVisitWithFocusGoingBackAndForthIsOneMiss()
    {
        // Twenty trips to Outlook and back, each well inside the grace: one remote session, not twenty.
        var visits = Visits();
        var ended = new List<MissedVisit>();
        Collect(ended, visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff));
        for (var i = 0; i < 20; i++)
        {
            Advance(30);
            Collect(ended, visits.Observe(Outlook, capturing: false));
            Advance(45);
            Collect(ended, visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff));
        }

        Advance(30);
        Collect(ended, visits.Observe(Outlook, capturing: false));
        Assert.Empty(ended);

        Advance(91);
        var missed = Assert.IsType<MissedVisit>(visits.Observe(Outlook, capturing: false));
        Assert.Equal(RemoteToolKind.Anydesk, missed.Tool);
        Assert.Equal(T0, missed.StartedAt);
        Assert.Equal(MissReason.AutoStartOff, missed.Reason);

        // It ended when the tool last left the front, not when the grace ran out.
        Assert.Equal(T0 + TimeSpan.FromSeconds((20 * 75) + 30), missed.EndedAt);
        Assert.False(visits.Open);
    }

    [Fact]
    public void ComingBackAfterTheGraceIsASecondVisit()
    {
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(120);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        // The first one ends at this look, and the tool in front now opens the second.
        Assert.NotNull(visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff));
        Assert.True(visits.Open);
        Advance(120);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);
        Assert.NotNull(visits.Observe(Outlook, capturing: false));
    }

    [Fact]
    public void AnotherRemoteToolWithinTheGraceIsTheSameVisit()
    {
        // As the session would be: the trigger keeps one session across tools within the grace.
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(40);
        _ = visits.Observe(ScreenConnect, capturing: false, MissReason.AutoStartOff);
        Advance(40);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        var missed = Assert.IsType<MissedVisit>(visits.Observe(Outlook, capturing: false));
        Assert.Equal(RemoteToolKind.Anydesk, missed.Tool);
    }

    [Fact]
    public void AVisitWithASessionInItIsNotAMiss()
    {
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: true);
        Advance(600);
        _ = visits.Observe(Outlook, capturing: true);
        Advance(91);

        Assert.Null(visits.Observe(Outlook, capturing: false));
        Assert.False(visits.Open);
    }

    [Fact]
    public void ASessionStartedByHandPartWayThroughCapturesTheVisit()
    {
        // Auto-start off, then Ctrl+Alt+R: the session machine says a session began.
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(120);
        visits.SessionRunning();
        Advance(120);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        Assert.Null(visits.Observe(Outlook, capturing: false));
    }

    [Theory]
    [InlineData(MissReason.AutoStartOff)]
    [InlineData(MissReason.StartHeld)]
    [InlineData(MissReason.NoIndicator)]
    [InlineData(MissReason.Busy)]
    public void AMissKeepsTheReasonTheStartWasRefused(MissReason reason)
    {
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, reason);
        Advance(120);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        Assert.Equal(reason, visits.Observe(Outlook, capturing: false)!.Reason);
    }

    [Fact]
    public void TheFirstReasonIsKept()
    {
        // Why the visit began uncaptured is what the banner explains; a later refusal does not rename it.
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.StartHeld);
        Advance(30);
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(90);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        Assert.Equal(MissReason.StartHeld, visits.Observe(Outlook, capturing: false)!.Reason);
    }

    [Fact]
    public void AToolSwitchedOffInSettingsIsStillARemoteSessionMissedForThatReason()
    {
        Assert.Equal((RemoteToolKind.Splashtop, true), RemoteVisits.Sighting(Splashtop));

        var visits = Visits();
        _ = visits.Observe(Splashtop, capturing: false);
        Advance(300);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        var missed = visits.Observe(Outlook, capturing: false)!;
        Assert.Equal(RemoteToolKind.Splashtop, missed.Tool);
        Assert.Equal(MissReason.ToolSwitchedOff, missed.Reason);
    }

    [Fact]
    public void AWindowThatIsNoRemoteToolsOpensNoVisit()
    {
        Assert.Null(RemoteVisits.Sighting(Outlook));
        Assert.Null(RemoteVisits.Sighting(PowerShell));
        Assert.Null(RemoteVisits.Sighting(new ScopeDecision(CaptureScope.Excluded, "1password", null, "Not capturing: 1Password is excluded.")));
        Assert.Null(RemoteVisits.Sighting(null));

        var visits = Visits();
        _ = visits.Observe(PowerShell, capturing: false);
        _ = visits.Observe(Outlook, capturing: false);
        Assert.False(visits.Open);
    }

    [Fact]
    public void ABriefPassThroughIsInNeitherNumber()
    {
        // Under a minute in the tool: the same rule that discards an empty captured session that brief.
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(20);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);

        Assert.Null(visits.Observe(Outlook, capturing: false));
        Assert.False(visits.Open);
    }

    [Fact]
    public void TimeAwayFromTheToolDoesNotCountTowardsTheMinimum()
    {
        // 40 s in, 80 s away (inside the grace), 15 s in: 55 s in front, so still a pass-through.
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(40);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(80);
        _ = visits.Observe(AnyDesk, capturing: false);
        Advance(15);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);
        Assert.Null(visits.Observe(Outlook, capturing: false));

        // And the timer's looks while the tool stays in front add up: 5 s at a time, past the minimum.
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        for (var i = 0; i < 13; i++)
        {
            Advance(5);
            _ = visits.Observe(AnyDesk, capturing: false);
        }

        _ = visits.Observe(Outlook, capturing: false);
        Advance(91);
        Assert.NotNull(visits.Observe(Outlook, capturing: false));
    }

    [Fact]
    public void TheGraceInForceIsTheOneUsed()
    {
        // The technician's grace setting changes the trigger's in place; the visit follows it.
        var grace = TimeSpan.FromSeconds(30);
        var visits = new RemoteVisits(() => grace, TimeSpan.FromSeconds(60), _time);
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.AutoStartOff);
        Advance(120);
        _ = visits.Observe(Outlook, capturing: false);
        Advance(31);

        Assert.NotNull(visits.Observe(Outlook, capturing: false));
    }

    [Fact]
    public void AVisitOpenAtShutdownEndsThere()
    {
        var visits = Visits();
        _ = visits.Observe(AnyDesk, capturing: false, MissReason.NoIndicator);
        Advance(300);

        var missed = Assert.IsType<MissedVisit>(visits.End());
        Assert.Equal(T0 + TimeSpan.FromSeconds(300), missed.EndedAt);
        Assert.Equal(MissReason.NoIndicator, missed.Reason);
        Assert.Null(visits.End());
    }

    private void Advance(int seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private static void Collect(List<MissedVisit> into, MissedVisit? visit)
    {
        if (visit is not null)
        {
            into.Add(visit);
        }
    }
}
