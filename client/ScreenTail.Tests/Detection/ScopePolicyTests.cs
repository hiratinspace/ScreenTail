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

    // ---- the Windows shell ---------------------------------------------------------------------------

    /// <summary>The shell's own surfaces, each drawn by explorer.exe and none of them a folder window.</summary>
    public static TheoryData<string, bool> ShellSurfaces()
    {
        var data = new TheoryData<string, bool>();
        foreach (var className in new[]
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame",
            "XamlExplorerHostIslandWindow", "TaskSwitcherWnd", "ForegroundStaging", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland",
        })
        {
            data.Add(className, false);
            data.Add(className, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ShellSurfaces))]
    public void TheDesktopTaskbarAndTaskViewAreNeverPhotographed(string className, bool allWindows)
    {
        // 2026-10-12 review, S-2. "explorer" was an admin tool by process name alone, and the desktop is
        // an explorer window whose rectangle is every monitor. A click on it photographed every window
        // on screen, excluded ones included, because exclusions are judged on the window in front. "All
        // windows" does not change that: it widens which app may be photographed, not which pixels.
        var policy = new ScopePolicy(Shipped, new ScopeOptions { CaptureAllWindows = allWindows });

        var decision = policy.Decide(Window("explorer", "Program Manager", className) with { Handle = 0x10010 });

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
        Assert.Null(decision.ToolId);
        Assert.Equal(0, decision.Window);
        // The HUD says what it says for any out-of-scope explorer window: the app, nothing from the window.
        Assert.Equal(new ScopePolicy(Shipped).Decide(Window("explorer", "Run", "#32770")).Reason, decision.Reason);
    }

    [Fact]
    public void AFileExplorerWindowIsStillAnAdminTool()
    {
        // The half of the rule that was right: browsing C:\Windows\Temp during a session is support work.
        var decision = new ScopePolicy(Shipped).Decide(Window("explorer", "Temp", "CabinetWClass") with { Handle = 0x20020 });

        Assert.Equal(CaptureScope.AdminTool, decision.Scope);
        Assert.Equal("explorer", decision.ToolId);
        Assert.Equal(0x20020, decision.Window);
    }

    [Theory]
    [InlineData("#32770")]
    [InlineData("OperationStatusWindow")]
    [InlineData("Window")]
    [InlineData("CabinetWClassX")]
    [InlineData("")]
    public void ExplorerIsAnAdminToolOnlyInAFolderWindow(string className)
    {
        // explorer.exe draws the Run box, copy progress and property sheets too, and a class it could not
        // read is not evidence of a folder window either. Out of scope, clicks still logged.
        var decision = new ScopePolicy(Shipped).Decide(Window("explorer", "Run", className));

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
    }

    [Theory]
    [InlineData("cabinetwclass", true)]
    [InlineData("CABINETWCLASS", true)]
    [InlineData("progman", false)]
    [InlineData("WORKERW", false)]
    public void WindowClassesCompareTheWayWindowsComparesThem(string className, bool inScope)
    {
        // Windows registers class names without regard to case, so the rule has to read them the same way
        // or a shell window could slip past it by spelling.
        var decision = new ScopePolicy(Shipped, new ScopeOptions { CaptureAllWindows = !inScope }).Decide(Window("explorer", "x", className));

        Assert.Equal(inScope, decision.MayCaptureFrames);
    }

    [Theory]
    [InlineData("wallpaper64", "WorkerW")]
    [InlineData(null, "Progman")]
    public void AShellClassIsRefusedWhicheverProcessDrawsIt(string? process, string className)
    {
        // Wallpaper tools parent themselves under a WorkerW, and a process we cannot name is no better.
        var decision = new ScopePolicy(Shipped, new ScopeOptions { CaptureAllWindows = true })
            .Decide(Window("x", "", className) with { ProcessName = process });

        Assert.False(decision.MayCaptureFrames);
    }

    [Fact]
    public void ARegistryThatNamesTheShellAsARemoteToolStillCannotPhotographTheDesktop()
    {
        // The refusal sits before every match, so no entry, shipped or edited, can reach the desktop. Naming
        // the shell's process is refused on load, so this entry reaches the desktop by its class instead.
        var registry = RemoteToolRegistry.Load("""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "shell", "kind": "other", "display_name": "Shell", "processes": ["wallpaper"],
                           "window_class_patterns": ["^Progman$"] } ] }
            """);

        Assert.False(new ScopePolicy(registry).Decide(Window("explorer", "Program Manager", "Progman")).MayCaptureFrames);
        Assert.False(new ScopePolicy(registry).Decide(Window("other", "Program Manager", "Progman")).MayCaptureFrames);
    }

    [Fact]
    public void TheDesktopDoesNotKeepASessionAliveOrStartOne()
    {
        // Only a remote tool starts a session or holds one open, so this registry makes the desktop look like
        // one by its class. The refusal comes first, so the desktop still neither starts nor extends anything.
        var registry = RemoteToolRegistry.Load("""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "rdp", "kind": "rdp", "display_name": "RDP", "processes": ["mstsc"],
                           "window_class_patterns": ["^Progman$"] } ] }
            """);
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(registry), clock, TimeSpan.FromSeconds(90));
        var desktop = Window("explorer", "Program Manager", "Progman");

        Assert.False(trigger.Observe(desktop).Start);
        Assert.False(trigger.SessionRunning);

        Assert.True(trigger.Observe(Window("mstsc", "RECEPTION-02")).Start);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(trigger.Observe(desktop).Stop);
        clock.Advance(TimeSpan.FromSeconds(31));

        // 91 seconds since the remote tool, 31 since the desktop: the desktop did not refresh the grace.
        Assert.True(trigger.Observe(desktop).Stop);
    }

    [Theory]
    [InlineData("explorer", "")]
    [InlineData("EXPLORER", "")]
    [InlineData("explorer", " ")]
    public void AShellWindowWhoseClassCannotBeReadIsNotPhotographedEvenWithAllWindowsOn(string process, string className)
    {
        // A shell window with no readable class could be the desktop, and nothing shows it is not. Refused
        // like the desktop, so "all windows" cannot reach it either; the same out-of-scope words as any other.
        var policy = new ScopePolicy(Shipped, new ScopeOptions { CaptureAllWindows = true });

        var decision = policy.Decide(Window(process, "", className) with { Handle = 0x10010 });

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.False(decision.MayCaptureFrames);
        Assert.Equal(0, decision.Window);
    }

    [Fact]
    public void AnotherAppsWindowWhoseClassCannotBeReadIsStillCapturedWithAllWindowsOn()
    {
        // The refusal is for the shell's process only: "all windows" means every other app's windows.
        var policy = new ScopePolicy(Shipped, new ScopeOptions { CaptureAllWindows = true });

        Assert.True(policy.Decide(Window("notepad", "notes", "")).MayCaptureFrames);
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("EXPLORER")]
    public void ARemoteToolCannotBeTheShellsProcess(string process)
    {
        // A remote tool matches every window of its process and starts a session on it, whatever its class
        // patterns say, so naming the shell would make every folder window a support session.
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            Tools =
            [
                new ToolEntry { Id = "t", DisplayName = "T", Processes = ["x"] },
                new ToolEntry { Id = "shell", DisplayName = "Shell", Processes = ["x", process], WindowClassPatterns = ["^CabinetWClass$"] },
            ],
        });

        Assert.Contains(problems, p => p.Contains("shell", StringComparison.Ordinal) && p.Contains("shell's process", StringComparison.Ordinal));
    }

    [Fact]
    public void ARegistryThatNamesTheShellAsARemoteToolIsRefusedOnLoad()
    {
        var load = () => RemoteToolRegistry.Load("""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "files", "kind": "other", "display_name": "Files", "processes": ["explorer"] } ] }
            """);

        var refused = Assert.Throws<InvalidOperationException>(load);
        Assert.Contains("shell's process", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("Explorer")]
    public void AnAdminToolForTheShellProcessMustNameItsWindowClasses(string process)
    {
        // Without a class list the rule is the one S-2 found: every explorer window, desktop included.
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            Tools = [new ToolEntry { Id = "t", DisplayName = "T", Processes = ["x"] }],
            AdminTools = [new AdminToolEntry { Id = "files", DisplayName = "Files", Processes = [process] }],
        });

        Assert.Contains(problems, p => p.Contains("files", StringComparison.Ordinal) && p.Contains("window classes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("workerw")]
    [InlineData("")]
    [InlineData(" ")]
    public void AnAdminToolCannotListAShellOrBlankWindowClass(string className)
    {
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            Tools = [new ToolEntry { Id = "t", DisplayName = "T", Processes = ["x"] }],
            AdminTools = [new AdminToolEntry { Id = "files", DisplayName = "Files", Processes = ["explorer"], WindowClasses = ["CabinetWClass", className] }],
        });

        Assert.Contains(problems, p => p.Contains("files", StringComparison.Ordinal));
    }

    [Fact]
    public void AWindowClassListThatIsNullInTheFileIsTreatedAsMissing()
    {
        // A hand-edited "window_classes": null must not become "no restriction" by way of a crash path.
        var load = () => RemoteToolRegistry.Load("""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "t", "kind": "other", "display_name": "T", "processes": ["x"] } ],
              "admin_tools": [ { "id": "files", "display_name": "Files", "processes": ["explorer"], "window_classes": null } ] }
            """);

        var refused = Assert.Throws<InvalidOperationException>(load);
        Assert.Contains("window classes", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdminToolThatNamesNoClassesStillMatchesAnyOfItsWindows()
    {
        // The restriction is opt-in per entry: PowerShell is PowerShell in a console host or a terminal.
        var decision = new ScopePolicy(Shipped).Decide(Window("powershell", "Administrator", "ConsoleWindowClass"));

        Assert.Equal(CaptureScope.AdminTool, decision.Scope);
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
    public void ASessionInTheToolPastTheGraceKeepsRunning()
    {
        // The grace runs from when the tool loses focus, not from when it took it. A technician working in
        // one RDP window for ten minutes makes no foreground change, and the timer that lets an idle session
        // end used to end this one too, ninety seconds in (2026-10-12 review, c8 blocker).
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        var tool = Window("mstsc", "RECEPTION-02");
        Assert.True(trigger.Observe(tool).Start);

        for (var elapsed = 0; elapsed < 600; elapsed += 5)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.False(trigger.Tick(tool).Stop);
        }

        Assert.True(trigger.SessionRunning);
        Assert.Equal(TimeSpan.FromSeconds(90), trigger.Remaining);
    }

    [Fact]
    public void TheGraceRunsFromWhenTheToolLosesFocus()
    {
        // The control for the test above: ten minutes in the tool, then Outlook, and the full grace from
        // that moment, no less and no more.
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        var tool = Window("mstsc", "RECEPTION-02");
        var outlook = Window("outlook", "Inbox");
        trigger.Observe(tool);
        clock.Advance(TimeSpan.FromSeconds(600));
        Assert.False(trigger.Tick(tool).Stop);

        Assert.False(trigger.Observe(outlook).Stop);
        clock.Advance(TimeSpan.FromSeconds(89));
        Assert.False(trigger.Tick(outlook).Stop);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(trigger.Tick(outlook).Stop);
    }

    [Fact]
    public void ASessionStartedByHandKeepsRunningWhileItsOwnWindowIsInFront()
    {
        // Ctrl+Alt+R over a window no registry knows. That window is the session's tool for as long as it
        // is in front, so the session outlives the grace there, as it did before the trigger followed
        // manual starts.
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        var own = Window("vendorconsole", "Customer 4471") with { Handle = 4471 };
        Assert.True(trigger.Follow(running: true, window: own));

        for (var elapsed = 0; elapsed < 600; elapsed += 5)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.False(trigger.Tick(own).Stop);
        }

        Assert.True(trigger.SessionRunning);
    }

    [Fact]
    public void ASessionStartedByHandRunsOutItsGraceOnceItsWindowIsLeft()
    {
        // Leaving that window for one that is neither it nor a remote tool starts the grace, and coming
        // back to it, by a foreground change, keeps the session as returning to a tool does.
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        var own = Window("vendorconsole", "Customer 4471") with { Handle = 4471 };
        var outlook = Window("outlook", "Inbox") with { Handle = 5000 };
        trigger.Follow(running: true, window: own);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(trigger.Observe(outlook).Stop);
        clock.Advance(TimeSpan.FromSeconds(80));
        Assert.False(trigger.Observe(own).Stop);
        clock.Advance(TimeSpan.FromSeconds(80));
        Assert.False(trigger.Tick(own).Stop);

        Assert.False(trigger.Observe(outlook).Stop);
        clock.Advance(TimeSpan.FromSeconds(91));
        Assert.True(trigger.Tick(outlook).Stop);
        Assert.False(trigger.SessionRunning);
    }

    [Fact]
    public void AnotherProcesssWindowUnderAReusedHandleDoesNotKeepASessionStartedByHand()
    {
        // Windows hands a closed window's handle out again. The window a session was started in by hand is
        // known by its handle and its process together, so another application's window that happens to
        // get the same handle later does not keep the session, and its microphone, running (c8 review).
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        var own = Window("vendorconsole", "Customer 4471") with { Handle = 4471, ProcessId = 900 };
        var reused = Window("notepad", "Untitled") with { Handle = 4471, ProcessId = 901 };
        trigger.Follow(running: true, window: own);

        Assert.False(trigger.Observe(reused).Stop);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(trigger.Tick(reused).Stop);
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.True(trigger.Tick(reused).Stop);
        Assert.False(trigger.SessionRunning);
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

    // ---- following the session machine (S-6, T-22) ---------------------------------------------------

    [Fact]
    public void AStartThatDidNotBeginIsTriedAgainOnTheNextFocus()
    {
        // The trigger decides to start as the tool takes focus. When no session then begins (nothing on
        // screen to say capture is on, auto-start off), the coordinator tells it so, and the next focus
        // is a start again rather than "no change" until a whole grace has passed (2026-10-12 review, T-22).
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        Assert.True(trigger.Observe(Window("mstsc", "RECEPTION-02")).Start);

        Assert.True(trigger.Follow(running: false));

        Assert.False(trigger.SessionRunning);
        Assert.Equal(TimeSpan.Zero, trigger.Remaining);
        Assert.True(trigger.Observe(Window("mstsc", "RECEPTION-02")).Start);
    }

    [Fact]
    public void ASessionStartedByHandAwayFromAnyToolRunsOutItsGrace()
    {
        // Ctrl+Alt+R over Outlook. The trigger hears of the start and the grace runs from it, so the session
        // and its microphone end once it is out (2026-10-12 review, S-6).
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        Assert.True(trigger.Follow(running: true));
        Assert.False(trigger.Observe(Window("outlook", "Inbox")).Stop);

        clock.Advance(TimeSpan.FromSeconds(89));
        Assert.False(trigger.Tick().Stop);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(trigger.Tick().Stop);
        Assert.False(trigger.SessionRunning);
    }

    [Fact]
    public void FollowingWhatTheTriggerAlreadyKnowsChangesNothing()
    {
        // The machine announces the start the trigger itself asked for. The tool and the grace it was
        // already counting are kept, rather than reset by its own echo.
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        trigger.Observe(Window("mstsc", "RECEPTION-02"));
        clock.Advance(TimeSpan.FromSeconds(60));
        trigger.Observe(Window("outlook", "Inbox"));

        Assert.False(trigger.Follow(running: true));

        Assert.Equal("rdp", trigger.RunningToolId);
        Assert.InRange(trigger.Remaining, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31));
        Assert.False(new SessionTrigger(new ScopePolicy(Shipped), clock).Follow(running: false));
    }

    [Fact]
    public void AStopFromTheTrayLeavesAutoStartArmed()
    {
        // Stopped by hand while still in the tool. The trigger follows, so the next remote-tool focus
        // starts a session instead of being taken for the one that already ended (S-6).
        var clock = new ManualTime(At);
        var trigger = new SessionTrigger(new ScopePolicy(Shipped), clock, TimeSpan.FromSeconds(90));
        trigger.Observe(Window("mstsc", "RECEPTION-02"));

        Assert.True(trigger.Follow(running: false));

        Assert.False(trigger.Tick().Stop);
        Assert.True(trigger.Observe(Window("mstsc", "RECEPTION-03")).Start);
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

    [Fact]
    public void ARegistryThatWouldCaptureEverythingDoesNotLoad()
    {
        // Validate reporting the problem is proved above; this is the other half, that Load refuses the
        // document rather than handing back a registry with the problem in it (2026-10-12 review, T-19).
        var load = () => RemoteToolRegistry.Load("""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "bad", "kind": "other", "display_name": "Bad", "window_class_patterns": [".*"] } ] }
            """);

        var refused = Assert.Throws<InvalidOperationException>(load);
        Assert.Contains("matches everything", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".*")]
    [InlineData(".+")]
    [InlineData("(?i)^.")]
    public void ABrowserPatternThatMatchesEveryTabIsRejected(string pattern)
    {
        // A browser entry with no url_pattern matches on the tab title alone, so a title pattern that
        // matches any title makes every tab a remote tool (2026-10-12 review, T-20). Refused by Validate
        // and so by Load, which a tenant's policy sync goes through too.
        var document = new RegistryDocument
        {
            Tools = [new ToolEntry { Id = "t", DisplayName = "T", Processes = ["x"] }],
            BrowserPatterns = [new BrowserPatternEntry { Id = "web", DisplayName = "Web", TitlePattern = pattern }],
        };

        var problems = RemoteToolRegistry.Validate(document);

        Assert.Contains(problems, p => p.Contains("web", StringComparison.Ordinal) && p.Contains("matches everything", StringComparison.Ordinal));
        var refused = Assert.Throws<InvalidOperationException>(() => RemoteToolRegistry.Load($$"""
            { "version": "t", "grace_seconds": 90,
              "tools": [ { "id": "t", "kind": "other", "display_name": "T", "processes": ["x"] } ],
              "browser_patterns": [ { "id": "web", "display_name": "Web", "title_pattern": {{System.Text.Json.JsonSerializer.Serialize(pattern)}} } ] }
            """));
        Assert.Contains("matches everything", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrowserPatternThatNamesItsToolIsAccepted()
    {
        // The control for the test above: the check is for patterns that match anything, not for every
        // title-only entry. One that names the tool, as the shipped entries do, loads.
        var problems = RemoteToolRegistry.Validate(new RegistryDocument
        {
            Tools = [new ToolEntry { Id = "t", DisplayName = "T", Processes = ["x"] }],
            BrowserPatterns = [new BrowserPatternEntry { Id = "web", DisplayName = "Web", TitlePattern = @"(?i)\bsplashtop (business|web)\b" }],
        });

        Assert.Empty(problems);
    }

    // ---- WSLg is not a remote desktop ---------------------------------------------------------------

    [Theory]
    [InlineData("RAIL_WINDOW")]
    [InlineData("Window")]
    public void AWslgWindowIsNotARemoteDesktopSession(string className)
    {
        // WSLg shows Linux GUI apps through its own copy of msrdc, in RAIL_WINDOW windows exactly like a
        // RemoteApp, so a Linux terminal used to auto-start an RDP session (2026-10-12 review, S-10). The
        // watcher reports that copy by the name "wslg"; the registry must not reach it by process or by
        // window class either.
        var decision = new ScopePolicy(Shipped).Decide(Window("wslg", "Terminal", className));

        Assert.Equal(CaptureScope.OutOfScope, decision.Scope);
        Assert.Null(decision.ToolId);
        Assert.False(decision.MayCaptureFrames);
    }

    [Fact]
    public void AWslgWindowCanBeExcludedByItsOwnName()
    {
        // Told apart, it can be excluded without excluding Azure Virtual Desktop's msrdc with it.
        var options = new ScopeOptions { ExcludedProcesses = new HashSet<string>(["wslg"], StringComparer.OrdinalIgnoreCase) };
        var policy = new ScopePolicy(Shipped, options);

        Assert.Equal(CaptureScope.Excluded, policy.Decide(Window("wslg", "Files", "RAIL_WINDOW")).Scope);
        Assert.Equal(CaptureScope.RemoteTool, policy.Decide(Window("msrdc", "Acme desktop", "RAIL_WINDOW")).Scope);
    }

    [Theory]
    [InlineData("msrdc", "RAIL_WINDOW")]
    [InlineData("msrdc", "TscShellContainerClass")]
    [InlineData("RdClient", "Windows.UI.Core.CoreWindow")]
    [InlineData("mstsc", "RAIL_WINDOW")]
    public void AzureVirtualDesktopAndWindows365AreStillRemoteDesktop(string process, string className)
    {
        var decision = new ScopePolicy(Shipped).Decide(Window(process, "Acme desktop", className));

        Assert.Equal(CaptureScope.RemoteTool, decision.Scope);
        Assert.Equal("rdp", decision.ToolId);
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
