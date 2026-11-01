using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Hud;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// A live region is announced when it changes (Spec §7, ST-084). Heard the way Narrator hears it: a UI
/// Automation client on the test's thread subscribes to LiveRegionChanged on the window, and the window's
/// thread changes what the region says. WPF raises nothing by itself, so until LiveRegion existed every
/// one of the views' live regions was silent.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class LiveRegionTests(TheApplication app)
{
    [Fact]
    public async Task TheShellsCaptureStateIsAnnouncedWhenItChanges()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        ShellWindow? shell = null;
        try
        {
            var handle = await app.RunAsync(async () =>
            {
                shell = await Screens.ShellAsync(_ => { });
                return new WindowInteropHelper(shell).Handle;
            });

            var heard = await HeardAsync(handle, () => app.RunAsync(() => shell!.Sample!.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "preview", Reason = "user" })));
            Assert.Contains(heard, name => name.StartsWith("Capture state: Paused", StringComparison.Ordinal));
        }
        finally
        {
            await app.RunAsync(() => shell?.Close());
        }
    }

    [Fact]
    public async Task ThePillIsAnnouncedWhenItsStateChangesAndNotEverySecond()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        HudWindow? pill = null;
        ShellState? state = null;
        try
        {
            var handle = await app.RunAsync(async () =>
            {
                state = new ShellState();
                state.Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 1_000 });
                pill = new HudWindow { DataContext = new HudViewModel(state), WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                pill.Show();
                await Audit.Settle();
                return new WindowInteropHelper(pill).Handle;
            });

            var ticking = await HeardAsync(handle, () => app.RunAsync(() => state!.Observe(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 2_000 })));
            Assert.Empty(ticking);

            var paused = await HeardAsync(handle, () => app.RunAsync(() => state!.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", Reason = "user" })));
            Assert.Contains("Paused: press Ctrl+Alt+P to resume", paused);
        }
        finally
        {
            await app.RunAsync(() => pill?.Close());
        }
    }

    /// <summary>The names of what announced itself in the window while <paramref name="change"/> ran, and a second after.</summary>
    private static async Task<IReadOnlyList<string>> HeardAsync(IntPtr window, Func<Task> change)
    {
        var heard = new ConcurrentQueue<string>();
        var root = await Task.Run(() => AutomationElement.FromHandle(window));
        AutomationEventHandler handler = (sender, _) =>
        {
            if (sender is AutomationElement element)
            {
                heard.Enqueue(element.Current.Name);
            }
        };
        await Task.Run(() => Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, root, TreeScope.Subtree, handler));
        try
        {
            await change();
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await Task.Run(() => Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, root, handler));
        }

        return [.. heard];
    }
}
