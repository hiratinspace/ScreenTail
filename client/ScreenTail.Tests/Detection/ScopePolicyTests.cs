using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Detection;

/// <summary>ST-023: what counts as a support session, what may be photographed, and when a session starts and stops.</summary>
public sealed class ScopePolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly RemoteToolRegistry Shipped = RemoteToolRegistry.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    // ---- the registry we actually ship --------------------------------------------------------------

    [Fact]
    public void TheShippedRegistryCoversTheToolsTheTicketAsksFor()
    {
        Assert.Equal(5, Shipped.Tools.Count);
        Assert.Equal(3, Shipped.BrowserPatterns.Count);
        Assert.Equal(
            ["anydesk", "rdp", "screenconnect", "splashtop", "teamviewer"],
            Shipped.Tools.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(90), Shipped.Grace);
        Assert.All(Shipped.Tools, t => Assert.NotEqual(RemoteToolKind.Other, t.Kind));
    }

    [Theory]
    [InlineData("ScreenConnect.WindowsClient", "screenconnect", RemoteToolKind.Screenconnect)]
    [InlineData("mstsc", "rdp", RemoteToolKind.Rdp)]
    [InlineData("msrdc", "rdp", RemoteToolKind.Rdp)]
    [InlineData("TeamViewer", "teamviewer", RemoteToolKind.Teamviewer)]
    [InlineData("AnyDesk", "anydesk", RemoteToolKind.Anydesk)]
    [InlineData("strwinclt", "splashtop", RemoteToolKind.Splashtop)]
    public void ARemoteToolWindowIsInScope(string process, string expectedId, RemoteToolKind expectedKind)
    {
        var decision = new ScopePolicy(Shipped).Decide(Window(process, "whatever"));

        Assert.Equal(CaptureScope.RemoteTool, decision.Scope);
        Assert.Equal(expectedId, decision.ToolId);
        Assert.Equal(expectedKind, decision.Tool);
        Assert.True(decision.MayCaptureFrames);
    }

    [Fact]
    public void AToolIsRecognisedByWindowClassWhenTheProcessIsUnknown()
    {
        // RDP's window class is stable even when the executable is renamed or wrapped by an RMM.
        var decision = new ScopePolicy(Shipped).Decide(
            Window("some-rmm-wrapper", "RECEPTION-02 — Remote Desktop", className: "TscShellContainerClass"));

        Assert.Equal(CaptureScope.RemoteTool, decision.Scope);
        Assert.Equal("rdp", decision.ToolId);
    }

    [Theory]
    [InlineData("Azure Virtual Desktop — Acme - Google Chrome", "https://client.wvd.microsoft.com/arm/webclient/", "azure-virtual-desktop")]
    [InlineData("Windows 365 - Microsoft Edge", "https://windows365.microsoft.com/", "azure-virtual-desktop")]
    [InlineData("ConnectWise Control — RECEPTION-02 - Google Chrome", "https://acme.screenconnect.com/Host#access", "screenconnect-web")]
    [InlineData("Splashtop Business - Google Chrome", "https://my.splashtop.com/computers", "splashtop-web")]
    public void ARemoteSessionInABrowserTabIsInScope(string title, string url, string expectedId)
    {
        var decision = new ScopePolicy(Shipped).Decide(Browser(title, url));

        Assert.Equal(CaptureScope.RemoteTool, decision.Scope);
        Assert.Equal(expectedId, decision.ToolId);
        Assert.Equal(RemoteToolKind.Browser, decision.Tool);
    }

    [Theory]
    [InlineData("ConnectWise Control pricing — Google Search - Google Chrome", "https://www.google.com/search?q=connectwise+control")]
    [InlineData("Your ScreenConnect invoice — Inbox - Google Chrome", "https://mail.google.com/mail/u/0/")]
    [InlineData("Splashtop Business documentation - Google Chrome", "https://support-splashtopbusiness.splashtop.com/hc/en-us")]
    [InlineData("Azure Virtual Desktop overview | Microsoft Learn - Microsoft Edge", "https://learn.microsoft.com/azure/virtual-desktop/")]
    public void APageAboutAToolIsNotASessionWithIt(string title, string url)
    {
        // 2026-09-19 review. Matching on the title alone put the whole browser in scope and started a
        // session, so a technician reading about a tool had their browsing captured (INV-5). Every one
        // of these is a page somebody would have open during an ordinary working day.
        var decision = new ScopePolicy(Shipped).Decide(Browser(title, url));

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
    }

    [Fact]
    public void ATabWhoseAddressCannotBeReadIsNotASession()
    {
        // Nothing reads a browser's address bar yet (ST-043), so this is the behaviour today: browser
        // entries do not match at all. A feature turned off rather than a wrong answer given, and
        // Ctrl+Alt+R still starts a session by hand.
        var decision = new ScopePolicy(Shipped).Decide(Browser("ConnectWise Control — RECEPTION-02 - Google Chrome"));

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
    }

    [Theory]
    [InlineData("Inbox — Outlook - Google Chrome")]
    [InlineData("Acme Dental — tickets - Google Chrome")]
    [InlineData("BBC News - Google Chrome")]
    public void AnOrdinaryBrowserTabIsNotASupportSession(string title)
    {
        var decision = new ScopePolicy(Shipped).Decide(Browser(title));

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
    }

    // ---- INV-5 ---------------------------------------------------------------------------------------

    [Fact]
    public void SwitchingToOutlookLogsClicksButCapturesNoFrames()
    {
        // The acceptance criterion, and the shape of INV-5: the timeline stays honest about where the
        // technician went, and no pixel of their inbox is stored.
        var decision = new ScopePolicy(Shipped).Decide(Window("outlook", "Inbox — sarah.jones@acmedental.example"));

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
        Assert.Equal("Not capturing — outlook", decision.Reason);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("mmc")]
    [InlineData("regedit")]
    [InlineData("Taskmgr")]
    public void AnAllowlistedAdminToolIsCaptured(string process)
    {
        var decision = new ScopePolicy(Shipped).Decide(Window(process, "Administrator"));

        Assert.Equal(CaptureScope.AdminTool, decision.Scope);
        Assert.True(decision.MayCaptureFrames);
    }

    [Fact]
    public void AllWindowsIsOptInAndChangesTheAnswer()
    {
        var window = Window("outlook", "Inbox");

        Assert.False(new ScopePolicy(Shipped).Decide(window).MayCaptureFrames);
        Assert.True(new ScopePolicy(Shipped, new ScopeOptions { CaptureAllWindows = true }).Decide(window).MayCaptureFrames);
    }

    [Fact]
    public void AnExcludedProcessBeatsEverythingElse()
    {
        // Even a remote tool: if the technician excluded it, it is not captured, no matter what it is.
        var options = new ScopeOptions { ExcludedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mstsc" } };

        var decision = new ScopePolicy(Shipped, options).Decide(Window("mstsc", "RECEPTION-02"));

        Assert.Equal(CaptureScope.Excluded, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
    }

    [Fact]
    public void AnElevatedWindowIsNeverPromisedFrames()
    {
        // Windows hides elevated windows from us; calling it in scope would promise frames that never come.
        var decision = new ScopePolicy(Shipped).Decide(Window("mmc", "Services") with { IsElevated = true });

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.Equal("Elevated window — screen not captured.", decision.Reason);
    }

    [Fact]
    public void TheReasonNamesTheAppAndNothingFromTheWindow()
    {
        // INV-10: the HUD says which app, never what was in it.
        var decision = new ScopePolicy(Shipped).Decide(Window("outlook", "Re: patient records for Mrs Hendry"));

        Assert.DoesNotContain("Hendry", decision.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("patient", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outlook", decision.Reason, StringComparison.Ordinal);
    }

    // ---- starting and stopping ------------------------------------------------------------------------

    [Fact]
    public void AScreenConnectWindowStartsASession()
    {
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock);

        var decision = trigger.Observe(Window("ScreenConnect.WindowsClient", "Acme Dental / RECEPTION-02"));

        Assert.True(decision.Start);
        Assert.Equal("screenconnect", decision.ToolId);
        Assert.True(trigger.SessionRunning);
    }

    [Fact]
    public void AnAvdTabStartsASessionToo()
    {
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock);

        Assert.True(trigger.Observe(Browser(
            "Azure Virtual Desktop — Acme - Google Chrome",
            "https://client.wvd.microsoft.com/arm/webclient/")).Start);
    }

    [Fact]
    public void SteppingIntoOutlookDoesNotEndTheSession()
    {
        // Reading the ticket is part of the same job. Ending the session here would cut one piece of work
        // into several notes, which is the failure the grace period exists to prevent.
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        trigger.Observe(Window("mstsc", "RECEPTION-02"));

        clock.Advance(TimeSpan.FromSeconds(30));
        var decision = trigger.Observe(Window("outlook", "Inbox"));

        Assert.False(decision.Stop);
        Assert.True(trigger.SessionRunning);
        Assert.InRange(trigger.Remaining, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(61));
    }

    [Fact]
    public void TheSessionStopsOnceTheGraceRunsOut()
    {
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        trigger.Observe(Window("mstsc", "RECEPTION-02"));

        clock.Advance(TimeSpan.FromSeconds(89));
        Assert.False(trigger.Observe(Window("outlook", "Inbox")).Stop);

        clock.Advance(TimeSpan.FromSeconds(2));
        var stopped = trigger.Tick();

        Assert.True(stopped.Stop);
        Assert.Equal("rdp", stopped.ToolId);
        Assert.False(trigger.SessionRunning);
    }

    [Fact]
    public void AnIdleDesktopStillEndsTheSession()
    {
        // A technician who walks away leaves no foreground changes at all, so the grace has to expire on a
        // timer rather than on the next window.
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        trigger.Observe(Window("mstsc", "RECEPTION-02"));

        clock.Advance(TimeSpan.FromSeconds(120));

        Assert.True(trigger.Tick().Stop);
    }

    [Fact]
    public void ReturningToTheToolKeepsTheSessionAlive()
    {
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        trigger.Observe(Window("mstsc", "RECEPTION-02"));

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            trigger.Observe(Window("outlook", "Inbox"));
            clock.Advance(TimeSpan.FromSeconds(10));
            trigger.Observe(Window("mstsc", "RECEPTION-02"));
        }

        Assert.True(trigger.SessionRunning);
    }

    [Fact]
    public void AdminToolsAloneDoNotStartASession()
    {
        // PowerShell is captured during a session; opening it on its own is not support work.
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), new ManualTime(At));

        Assert.False(trigger.Observe(Window("powershell", "Administrator")).Start);
        Assert.False(trigger.SessionRunning);
    }

    // ---- the registry as hostile input ----------------------------------------------------------------

    [Fact]
    public void ARegistryThatWouldCaptureEverythingIsRejected()
    {
        // This is the security boundary: a pattern matching every window turns a support tool into a
        // screen recorder. A tenant policy sync (ST-047) gets the same check.
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            Tools = [new ToolEntry { Id = "bad", DisplayName = "Bad", WindowClassPatterns = [".*"] }],
        });

        Assert.Contains(problems, p => p.Contains("matches everything", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "grace")]
    [InlineData(4, "grace")]
    [InlineData(6000, "grace")]
    public void AnAbsurdGraceIsRejected(int seconds, string expected)
    {
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            GraceSeconds = seconds,
            Tools = [new ToolEntry { Id = "t", DisplayName = "T", Processes = ["x"] }],
        });

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyRegistryIsRejectedRatherThanSilentlyCapturingNothing()
    {
        var problems = RemoteToolRegistry.Validate(new RegistryDocument());

        Assert.Contains(problems, p => p.Contains("no remote tools", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInvalidPatternIsRejected()
    {
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            Tools = [new ToolEntry { Id = "bad", DisplayName = "Bad", WindowClassPatterns = ["^(unclosed"] }],
        });

        Assert.Contains(problems, p => p.Contains("invalid pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunawayPatternCannotStallTheWatcher()
    {
        // Scope is decided on every foreground change, so a pathological pattern would stall detection.
        // It gets a budget, and a pattern that exceeds it matches nothing rather than blocking.
        var registry = RemoteToolRegistry.Load("""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "slow", "kind": "other", "display_name": "Slow",
                           "window_class_patterns": ["^(a+)+$"] } ] }
            """);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var decision = new ScopePolicy(registry).Decide(Window("unknown", "x", className: new string('a', 40) + "!"));
        clock.Stop();

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.True(clock.ElapsedMilliseconds < 1_000, $"took {clock.ElapsedMilliseconds} ms");
    }

    private static ForegroundWindowInfo Window(string process, string title, string className = "Window") =>
        new(1, 100, process, title, className, BrowserTitles.ActiveTab(process, title), false, At);

    /// <param name="url">
    /// The tab's address. Required for a browser entry that declares one, because a title is not
    /// evidence of what a tab is: a support email, a search result and the vendor's own documentation
    /// all carry the tool's name.
    /// </param>
    private static ForegroundWindowInfo Browser(string title, string? url = null) =>
        Window("chrome", title) with { BrowserUrl = url };

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
