using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation.Peers;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Controls;
using ScreenTail.UI.Hud;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// A live region is announced when it changes (Spec §7, ST-084). WPF raises nothing by itself, so until
/// <see cref="LiveRegion"/> existed every live region in the views was silent. These listen where
/// LiveRegion hands each announcement to UI Automation and check what a screen reader would be told: the
/// name of the element, as its automation peer gives it.
///
/// Heard through LiveRegion's own event rather than through a UI Automation client in this process,
/// which WPF does not count as a listener; whether Narrator then reads it aloud is on the manual list.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class LiveRegionTests(TheApplication app)
{
    [Fact]
    public async Task TheShellsCaptureStateIsAnnouncedWhenItChanges()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.ShellAsync(_ => { });
            try
            {
                var heard = await HeardAsync(() => shell.Sample!.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "preview", Reason = "user" }));
                Assert.Contains(heard, name => name.StartsWith("Capture state: Paused", StringComparison.Ordinal));
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task ThePillIsAnnouncedWhenItsStateChangesAndNotEverySecond()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var state = new ShellState();
            state.Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 1_000 });
            var pill = new HudWindow { DataContext = new HudViewModel(state), WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            pill.Show();
            try
            {
                await Audit.Settle();
                var ticking = await HeardAsync(() => state.Observe(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 2_000 }));
                Assert.Empty(ticking);

                var paused = await HeardAsync(() => state.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", Reason = "user" }));
                Assert.Contains("Paused: press Ctrl+Alt+P to resume", paused);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    /// <summary>A banner that appears is announced too, though its own words did not change.</summary>
    [Fact]
    public async Task TheServiceDownBannerIsAnnouncedWhenItAppears()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.ShellAsync(_ => { });
            try
            {
                var heard = await HeardAsync(() => shell.Sample!.Lost());
                Assert.Contains(heard, name => name.Contains("not running", StringComparison.OrdinalIgnoreCase) || name.Contains("capture service", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>What was announced, by automation name, while <paramref name="change"/> ran and the dispatcher caught up.</summary>
    private static async Task<IReadOnlyList<string>> HeardAsync(Action change)
    {
        var heard = new List<string>();
        void Listen(UIElement element) => heard.Add(UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? string.Empty);
        LiveRegion.Announced += Listen;
        try
        {
            change();
            for (var i = 0; i < 10; i++)
            {
                await Audit.Settle();
            }
        }
        finally
        {
            LiveRegion.Announced -= Listen;
        }

        return heard;
    }
}
