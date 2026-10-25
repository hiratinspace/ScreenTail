using System.Runtime.Versioning;
using System.Windows;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.UI.Hud;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The pill comes back where it can be seen, judged against the real monitors (INV-4; 2026-10-12 review).
///
/// The indicator reports ask whether the pill's middle is on a real monitor. The re-dock asked about the
/// bounding box of all of them, so a pill saved in the dead space beside an uneven layout was left there:
/// no report went out, the service suppressed capture, and the technician saw no pill. Both now ask the
/// window's one question. The monitors are given to the window here, so the layout is the test's.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HudWindowPlacementTests
{
    [Fact]
    public void APillSavedWhereNoMonitorIsDocksToTheCorner()
    {
        // A point inside this machine's own desktop, so the bounding box of the real monitors (the old
        // check) would leave it alone, and on none of the monitors the window is told about.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(() =>
        {
            var file = Saved(100, 100);
            var window = new HudWindow(new ShellPreferencesStore(file), () => [new ScreenArea(10_000, 10_000, 11_920, 11_080)]);
            try
            {
                Assert.Equal((100d, 100d), (window.Left, window.Top));
                Assert.False(window.OnSomeMonitor());

                window.DockIfNowhere();

                var area = SystemParameters.WorkArea;
                Assert.Equal((area.Right - window.ActualWidth - 24, area.Top + 24), (window.Left, window.Top));
            }
            finally
            {
                window.Close();
                File.Delete(file);
            }
        });
    }

    [Fact]
    public void APillSavedOnAMonitorStaysWhereItWasPut()
    {
        // The control: a place on one of the monitors is the technician's arrangement, and is kept.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(() =>
        {
            var file = Saved(10_500, 10_500);
            var window = new HudWindow(new ShellPreferencesStore(file), () => [new ScreenArea(10_000, 10_000, 11_920, 11_080)]);
            try
            {
                Assert.True(window.OnSomeMonitor());

                window.DockIfNowhere();

                Assert.Equal((10_500d, 10_500d), (window.Left, window.Top));
            }
            finally
            {
                window.Close();
                File.Delete(file);
            }
        });
    }

    [Fact]
    public void TheRealMonitorsAreTheOnesWindowsReports()
    {
        // The default the shipped pill uses: one rectangle per monitor, each with an area.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var monitors = HudWindow.Monitors();

        Assert.Equal(System.Windows.Forms.Screen.AllScreens.Length, monitors.Count);
        Assert.All(monitors, m => Assert.True(m.Right > m.Left && m.Bottom > m.Top));
    }

    private static string Saved(double x, double y)
    {
        var file = Path.Combine(Path.GetTempPath(), $"screentail-hud-{Guid.NewGuid():N}.json");
        Assert.True(new ShellPreferencesStore(file).Save(new ShellPreferences { HudX = x, HudY = y }), "the test could not write its preferences file");
        return file;
    }
}
