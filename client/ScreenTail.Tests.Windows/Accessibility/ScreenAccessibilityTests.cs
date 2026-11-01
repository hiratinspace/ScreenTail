using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using ScreenTail.UI.Hud;
using ScreenTail.UI.Theme;
using ScreenTail.UI.Tray;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// ST-084's automated pass: Spec §7 on every screen a technician reaches, on a real shown window, on every
/// run of the hosted Windows job (and the laptop's).
///
/// Per screen, with the screenshot harness's sample data: every element a screen reader reads has a name
/// that is words, and every control has the role and pattern its kind needs, with its visible label in
/// its name; every control is at least 28 x 28; no button is mouse-only; Tab reaches every control in
/// reading order with no trap, focus never lands on something covered, Shift+Tab retraces it, and the
/// focused control visibly changes with 3:1 contrast, in the dark, light and high-contrast palettes; and
/// Accessibility Insights' automated rules (axe-windows) report no error (AC2).
///
/// What still needs a person is in docs/ux/a11y-audit.md: listening to Narrator read each screen.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class ScreenAccessibilityTests(TheApplication app)
{
    public static TheoryData<string> AllScreens => [.. Screens.All];

    /// <summary>The pill's variants, as HudPreview renders them; the test checks the list is complete.</summary>
    private static readonly string[] PillStateNames =
    [
        "recording", "paused-user", "suppressed-password", "suppressed-excluded", "suppressed-elevated",
        "out-of-scope", "no-mic", "offline", "unknown", "redacting",
    ];

    public static TheoryData<string> PillStates => [.. PillStateNames];

    [Theory]
    [MemberData(nameof(AllScreens))]
    public async Task EachScreenMeetsSpecSection7(string screen)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var findings = new List<string>();
        Window? window = null;
        try
        {
            var handle = await app.RunAsync(async () =>
            {
                window = await Screens.OpenAsync(screen);
                findings.AddRange(Audit.Names(window).Select(f => "name: " + f));
                findings.AddRange(Audit.HitTargets(window).Select(f => "hit target: " + f));
                findings.AddRange(Audit.MouseOnly(window).Select(f => "keyboard: " + f));
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
                {
                    ThemeManager.Apply(theme, app.Resources);
                    await Audit.Settle();
                    var tab = await Audit.TabAsync(window);
                    findings.AddRange(tab.Findings
                        .Where(f => theme == AppTheme.Dark || f.Contains("does not look focused", StringComparison.Ordinal))
                        .Select(f => $"keyboard ({theme}): {f}"));
                }

                ThemeManager.Apply(AppTheme.Dark, app.Resources);
                await Audit.Settle();
                return new WindowInteropHelper(window).Handle;
            });

            findings.AddRange((await AxeScan.ErrorsAsync(handle)).Select(f => "Accessibility Insights: " + f));
        }
        finally
        {
            await app.RunAsync(async () =>
            {
                ThemeManager.Apply(AppTheme.Dark, app.Resources);
                if (window is not null)
                {
                    await Screens.CloseAsync(window);
                }
            });
        }

        Assert.True(findings.Count == 0, $"{screen}: {findings.Count} findings\n" + string.Join('\n', findings.Distinct()));
    }

    /// <summary>
    /// Spec §5 S2 and §7 for the pill, expanded so Mark moment shows: named, its control 32 x 32, nothing
    /// mouse-only, no Accessibility Insights error, and it never takes focus from the remote session
    /// (its keyboard path is the global hotkeys, which the shortcut sheet lists).
    /// </summary>
    [Theory]
    [MemberData(nameof(PillStates))]
    public async Task ThePillInEachStateMeetsSpecSection7(string state)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var findings = new List<string>();
        HudWindow? pill = null;
        try
        {
            var handle = await app.RunAsync(async () =>
            {
                Assert.Equal<string>(PillStateNames, [.. HudPreview.Variants().Select(v => v.Name)]);
                var model = HudPreview.Variants().Single(v => v.Name == state).Model;
                model.Expanded = true;
                pill = new HudWindow { DataContext = model, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                pill.Show();
                for (var i = 0; i < 5; i++)
                {
                    await Audit.Settle();
                }

                findings.AddRange(Audit.Names(pill).Select(f => "name: " + f));
                findings.AddRange(Audit.HitTargets(pill, Audit.HudHitTarget).Select(f => "hit target: " + f));
                findings.AddRange(Audit.MouseOnly(pill).Select(f => "keyboard: " + f));
                if (pill.IsActive || pill.IsKeyboardFocusWithin)
                {
                    findings.Add("focus: the pill took focus when it appeared");
                }

                return new WindowInteropHelper(pill).Handle;
            });

            findings.AddRange((await AxeScan.ErrorsAsync(handle)).Select(f => "Accessibility Insights: " + f));
        }
        finally
        {
            await app.RunAsync(() => pill?.Close());
        }

        Assert.True(findings.Count == 0, $"pill {state}: {findings.Count} findings\n" + string.Join('\n', findings.Distinct()));
    }

    /// <summary>
    /// Spec §5 S1's quick menu: every item is named for a screen reader, is a menu item, and is at least
    /// 28 px tall. It is a Windows menu, so the arrows, Enter and Esc work in it the way they do in every
    /// other tray menu; opening it from the keyboard (Win+B, then the menu key) is on the manual list.
    /// </summary>
    [Fact]
    public async Task TheTrayMenuMeetsSpecSection7()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var findings = await app.RunAsync(() =>
        {
            var found = new List<string>();
            using var tray = new TrayIconHost();
            var menu = tray.Menu;
            _ = menu.Handle;
            menu.PerformLayout();
            var scale = menu.DeviceDpi / 96.0;
            foreach (var item in menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>())
            {
                var accessible = item.AccessibilityObject;
                if (string.IsNullOrWhiteSpace(accessible.Name))
                {
                    found.Add($"a menu item has no name");
                }

                if (accessible.Role != System.Windows.Forms.AccessibleRole.MenuItem)
                {
                    found.Add($"\"{accessible.Name}\" is exposed as {accessible.Role}, not a menu item");
                }

                var height = item.Height / scale;
                if (item.Enabled && height + 0.5 < Audit.HitTarget)
                {
                    found.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"{accessible.Name}\" is {height:0.#} px tall, under {Audit.HitTarget}"));
                }
            }

            return Task.FromResult(found);
        });

        Assert.True(findings.Count == 0, string.Join('\n', findings));
    }
}
