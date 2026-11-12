using ScreenTail.Core.Hud;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Hud;

/// <summary>
/// Spec §5 S2 (v0.6.0): the expanded pill shows the session's tool and start, and the last three events
/// as types and times only. Built from what the UI already hears from the service (the capture state
/// snapshots) and from the marks the pill itself sends; nothing here asks the service for more.
///
/// INV-10: the pill sits over a customer's desktop, so an event is a kind, a count and a time. Never a
/// window title, a process name, screen text, transcript, company or ticket.
/// </summary>
public sealed class HudTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 14, 2, 0, TimeSpan.Zero);

    [Fact]
    public void ANewSessionIsTitledByItsToolAndWhenItStarted()
    {
        var time = new Clock(Start.AddSeconds(30));
        var timeline = new HudTimeline(time);

        // First heard 30 seconds in: the start is the arrival less the elapsed time.
        timeline.Observe(Recording(pending: 0, elapsedMs: 30_000));

        Assert.Equal("ScreenConnect session, since 14:02", timeline.SessionTitle);
        Assert.Empty(timeline.Recent);
    }

    [Fact]
    public void AToolThisVersionDoesNotKnowStillGetsATitle()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(Recording(pending: 0) with { RemoteTool = null });

        Assert.Equal("Remote session, since 14:02", timeline.SessionTitle);
    }

    [Fact]
    public void AScreenshotIsTakenWhenTheBacklogGrowsAndMaskedWhenItShrinks()
    {
        var time = new Clock(Start);
        var timeline = new HudTimeline(time);
        timeline.Observe(Recording(pending: 0));

        time.Now = Start.AddMinutes(11).AddSeconds(51);
        timeline.Observe(Recording(pending: 1));
        time.Now = Start.AddMinutes(12).AddSeconds(3);
        timeline.Observe(Recording(pending: 0));

        Assert.Equal(["1 screenshot masked", "Screenshot taken"], timeline.Recent.Select(e => e.Text));
        Assert.Equal(["14:14:03", "14:13:51"], timeline.Recent.Select(e => e.Time));
        Assert.Equal("1 screenshot masked, 14:14:03", timeline.Recent[0].Spoken);
    }

    [Fact]
    public void SeveralAtOnceAreCounted()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(Recording(pending: 0));
        timeline.Observe(Recording(pending: 3));
        timeline.Observe(Recording(pending: 1));

        Assert.Equal(["2 screenshots masked", "3 screenshots taken"], timeline.Recent.Select(e => e.Text));
    }

    [Fact]
    public void OnlyTheLastThreeAreKeptNewestFirst()
    {
        var time = new Clock(Start);
        var timeline = new HudTimeline(time);
        timeline.Observe(Recording(pending: 0));
        for (var i = 1; i <= 5; i++)
        {
            time.Now = Start.AddSeconds(i);
            timeline.Observe(Recording(pending: i % 2));
        }

        Assert.Equal(HudTimeline.Shown, timeline.Recent.Count);
        Assert.Equal(["14:02:05", "14:02:04", "14:02:03"], timeline.Recent.Select(e => e.Time));
    }

    [Fact]
    public void PausingResumingAndAGuardAreEvents()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(Recording(pending: 0));
        timeline.Observe(Recording(pending: 0) with { State = CaptureStates.Paused, Reason = CaptureReasons.User });
        timeline.Observe(Recording(pending: 0));
        timeline.Observe(Recording(pending: 0) with { State = CaptureStates.Suppressed, Reason = CaptureReasons.PasswordField });

        Assert.Equal(["Paused automatically", "Resumed", "Paused"], timeline.Recent.Select(e => e.Text));
    }

    /// <summary>INV-10: out of scope names no process on the expanded pill, only the kind of event.</summary>
    [Fact]
    public void NotCapturingNamesNoProcess()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(Recording(pending: 0));
        timeline.Observe(Recording(pending: 0) with { State = CaptureStates.Suppressed, Reason = CaptureReasons.OutOfScope, ScopeProcess = "OUTLOOK" });

        var only = Assert.Single(timeline.Recent);
        Assert.Equal("Not capturing", only.Text);
        Assert.DoesNotContain("utlook", only.Spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMarkThePillSentIsAnEvent()
    {
        var time = new Clock(Start);
        var timeline = new HudTimeline(time);
        timeline.Observe(Recording(pending: 0));

        time.Now = Start.AddMinutes(11).AddSeconds(20);
        timeline.MomentMarked();

        var only = Assert.Single(timeline.Recent);
        Assert.Equal(HudEventKind.MomentMarked, only.Kind);
        Assert.Equal("Moment marked", only.Text);
        Assert.Equal("14:13:20", only.Time);
    }

    [Fact]
    public void AMarkOutsideASessionIsNotRecorded()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.MomentMarked();

        Assert.Empty(timeline.Recent);
    }

    [Fact]
    public void ANewSessionStartsAnEmptyList()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(Recording(pending: 0));
        timeline.Observe(Recording(pending: 1));

        timeline.Observe(Recording(pending: 0) with { SessionId = "s2", RemoteTool = "rdp" });

        Assert.Empty(timeline.Recent);
        Assert.Equal("Remote Desktop session, since 14:02", timeline.SessionTitle);
    }

    /// <summary>A dropped pipe is not an event, and it does not wipe what the pill last knew.</summary>
    [Fact]
    public void LosingTheServiceChangesNothing()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(Recording(pending: 0));
        timeline.Observe(Recording(pending: 1));

        Assert.False(timeline.Observe(null));
        Assert.Single(timeline.Recent);
    }

    [Fact]
    public void BetweenSessionsThereIsNoTitle()
    {
        var timeline = new HudTimeline(new Clock(Start));
        timeline.Observe(new CaptureStateSnapshot { State = CaptureStates.Idle });

        Assert.Null(timeline.SessionTitle);
        Assert.Empty(timeline.Recent);
    }

    /// <summary>INV-10 by construction: nothing the snapshot carries as content reaches an event's words.</summary>
    [Fact]
    public void NoContentReachesAnEvent()
    {
        var timeline = new HudTimeline(new Clock(Start));
        var content = Recording(pending: 0) with { DraftTitle = "Printer offline", DraftCompany = "Acme Dental", ScopeProcess = "OUTLOOK" };
        timeline.Observe(content);
        timeline.Observe(content with { PendingRedactions = 2 });
        timeline.Observe(content with { State = CaptureStates.Suppressed, Reason = CaptureReasons.OutOfScope });

        foreach (var spoken in timeline.Recent.Select(e => e.Spoken).Append(timeline.SessionTitle!))
        {
            Assert.DoesNotContain("Printer", spoken, StringComparison.Ordinal);
            Assert.DoesNotContain("Acme", spoken, StringComparison.Ordinal);
            Assert.DoesNotContain("OUTLOOK", spoken, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('—', spoken);
        }
    }

    private static CaptureStateSnapshot Recording(int pending, long elapsedMs = 0) => new()
    {
        State = CaptureStates.Recording,
        SessionId = "s1",
        ElapsedMs = elapsedMs,
        RemoteTool = "screenconnect",
        PendingRedactions = pending,
    };

    /// <summary>A wall clock the test sets, in UTC so the times read the same on every machine.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
