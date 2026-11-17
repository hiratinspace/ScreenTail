using ScreenTrail.Core.Detection;
using ScreenTrail.Core.Detection.Registry;
using ScreenTrail.Shared.Schema;

namespace ScreenTrail.Tests.Detection;

/// <summary>
/// ST-071, Spec §5 S1 as built v0.7.2: the window the tray's Start capture starts in. The menu is in front
/// as it is chosen, so the window in front is never the remote session; the remote tool window last seen in
/// front is, within a few minutes, while it is open, and while the policy in force still calls it a remote
/// tool (INV-5, INV-11).
/// </summary>
public sealed class RecentRemoteToolTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly RemoteToolRegistry Shipped = RemoteToolRegistry.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    private static readonly ForegroundWindowInfo ScreenConnect = Window(11, 100, "ScreenConnect.WindowsClient", "RECEPTION-02");
    private static readonly ForegroundWindowInfo AnyDesk = Window(12, 200, "AnyDesk", "Acme laptop");
    private static readonly ForegroundWindowInfo Outlook = Window(13, 300, "OUTLOOK", "Inbox");
    private static readonly ForegroundWindowInfo Taskbar = Window(14, 400, "explorer", string.Empty, "Shell_TrayWnd");
    private static readonly ForegroundWindowInfo ScreenTrailUi = Window(15, 500, "ScreenTrail.UI", string.Empty);

    [Fact]
    public void TheToolLastInFrontIsPickedOnceTheMenuHasTakenTheFront()
    {
        var (recent, policy, _) = Rig();
        Look(recent, policy, AnyDesk);
        Look(recent, policy, ScreenConnect);
        Look(recent, policy, Outlook);
        Look(recent, policy, Taskbar);
        Look(recent, policy, ScreenTrailUi);

        var picked = recent.Pick(policy, _ => true);

        Assert.NotNull(picked);
        Assert.Equal(ScreenConnect.Handle, picked.Value.Window.Handle);
        Assert.Equal("screenconnect", picked.Value.Decision.ToolId);
        Assert.Equal(RemoteToolKind.Screenconnect, picked.Value.Decision.Tool);
        Assert.Equal(CaptureScope.RemoteTool, picked.Value.Decision.Scope);
    }

    [Fact]
    public void WithNoRemoteToolSeenThereIsNothingToStartIn()
    {
        var (recent, policy, _) = Rig();
        Look(recent, policy, Outlook);
        Look(recent, policy, Taskbar);

        Assert.Null(recent.Pick(policy, _ => true));
    }

    [Fact]
    public void AToolLeftLongerAgoThanTheWindowIsNotPicked()
    {
        var (recent, policy, clock) = Rig();
        Look(recent, policy, ScreenConnect);
        Look(recent, policy, Outlook);

        clock.Advance(RecentRemoteTool.DefaultWithin);
        Assert.NotNull(recent.Pick(policy, _ => true));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(recent.Pick(policy, _ => true));
    }

    [Fact]
    public void ALookWhileTheToolIsStillInFrontKeepsItCurrent()
    {
        // The auto-stop timer looks every five seconds: a technician working in one window for an hour
        // makes no foreground change, and the tool is still the one they were in.
        var (recent, policy, clock) = Rig();
        for (var i = 0; i < 20; i++)
        {
            Look(recent, policy, ScreenConnect);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.NotNull(recent.Pick(policy, _ => true));
    }

    [Fact]
    public void AToolWindowThatHasClosedIsNotPicked()
    {
        var (recent, policy, _) = Rig();
        Look(recent, policy, ScreenConnect);

        Assert.Null(recent.Pick(policy, handle => handle != ScreenConnect.Handle));
    }

    [Fact]
    public void AToolSwitchedOffSinceItWasSeenIsNotPicked()
    {
        // INV-11, INV-5: the policy in force when the tray is chosen decides, not the one when it was seen.
        var (recent, policy, _) = Rig();
        Look(recent, policy, ScreenConnect);

        policy.Apply(policy.Options with { DisabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "screenconnect" } });

        Assert.Null(recent.Pick(policy, _ => true));
    }

    [Fact]
    public void AnAppExcludedSinceItWasSeenIsNotPicked()
    {
        var (recent, policy, _) = Rig();
        Look(recent, policy, AnyDesk);

        policy.Apply(policy.Options with { ExcludedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AnyDesk" } });

        Assert.Null(recent.Pick(policy, _ => true));
    }

    [Fact]
    public void TheSameWindowSeenAgainAsSomethingElseIsForgotten()
    {
        // A browser tab that was a remote session and is now a search page: the window is the same, what
        // it shows is not, so the tray does not start in it on the strength of what it was.
        var (recent, policy, _) = Rig();
        Look(recent, policy, ScreenConnect);
        var renamed = ScreenConnect with { ProcessName = "notepad", Title = "notes.txt" };
        Look(recent, policy, renamed);

        Assert.Null(recent.Pick(policy, _ => true));
    }

    [Fact]
    public void AnEmptyForegroundChangesNothing()
    {
        var (recent, policy, _) = Rig();
        Look(recent, policy, ScreenConnect);
        Look(recent, policy, ForegroundWindowInfo.None(At));

        Assert.Equal(ScreenConnect.Handle, recent.Pick(policy, _ => true)?.Window.Handle);
    }

    private static void Look(RecentRemoteTool recent, ScopePolicy policy, ForegroundWindowInfo window) => recent.Saw(window, policy.Decide(window));

    private static (RecentRemoteTool Recent, ScopePolicy Policy, ManualTime Clock) Rig()
    {
        var clock = new ManualTime(At);
        return (new RecentRemoteTool(clock), new ScopePolicy(Shipped, new ScopeOptions()), clock);
    }

    private static ForegroundWindowInfo Window(nint handle, int process, string name, string title, string className = "Window") =>
        new(handle, process, name, title, className, BrowserTitles.ActiveTab(name, title), false, At);
}
