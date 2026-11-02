using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTail.UI.Theme;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Spec §2.3 and §7: the app follows Windows' "Show animations" and high-contrast settings. Both settings
/// are simulated through <see cref="ThemeManager"/>'s seams rather than switched on the machine, which
/// would change them for everything else on the desktop (and on the laptop, for its owner).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class MotionAndContrastTests(TheApplication app)
{
    /// <summary>
    /// The screens where something changes on a key or a click: the timeline opens and closes, and Settings
    /// and the mapping prompt have combo boxes that drop down. Every screen is also checked still, as it
    /// opens, by <see cref="ScreenAccessibilityTests"/>.
    /// </summary>
    private static readonly string[] Moving = ["review", "settings", "review-needs-mapping"];

    /// <summary>
    /// With animations off in Windows, the app says reduce motion, its motion helper gives no duration,
    /// and nothing animates while the timeline opens and closes or while a combo box drops down. That no
    /// screen animates as it opens, and that no style or template declares a storyboard (which would run
    /// whatever the setting said), is held per screen by <see cref="ScreenAccessibilityTests"/>.
    /// </summary>
    [Fact]
    public async Task WithAnimationsOffNothingMoves()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var findings = new List<string>();
        var real = ThemeManager.ClientAreaAnimation;
        ThemeManager.ClientAreaAnimation = () => false;
        try
        {
            Assert.True(ThemeManager.ReduceMotion);
            Assert.Equal(TimeSpan.Zero, ThemeManager.Motion(new Duration(TimeSpan.FromMilliseconds(180))).TimeSpan);
            foreach (var screen in Moving)
            {
                await app.RunAsync(async () =>
                {
                    var window = await Screens.OpenAsync(screen);
                    try
                    {
                        if (screen == "review")
                        {
                            _ = Screens.Named<TextBox>(window, "Problem").Focus();
                            _ = Keys.Press(window, Key.T, ModifierKeys.Alt);
                            await Audit.Settle();
                            findings.AddRange(Audit.Moving(window).Select(f => $"{screen}, timeline toggled: {f}"));
                        }

                        foreach (var combo in Audit.Descendants(window).OfType<ComboBox>().Where(c => c.IsVisible && c.IsEnabled).Take(2))
                        {
                            combo.IsDropDownOpen = true;
                            await Audit.Settle();
                            findings.AddRange(Audit.Moving(window).Select(f => $"{screen}, a combo box open: {f}"));
                            combo.IsDropDownOpen = false;
                            await Audit.Settle();
                        }
                    }
                    finally
                    {
                        await Screens.CloseAsync(window);
                    }
                });
            }
        }
        finally
        {
            ThemeManager.ClientAreaAnimation = real;
        }

        Assert.True(findings.Count == 0, string.Join('\n', findings.Distinct()));
    }

    /// <summary>
    /// With Windows in high contrast, whatever theme the technician chose, the app applies the
    /// high-contrast palette and remembers their choice, and every colour token then resolves to a system
    /// colour. That every opaque colour drawn on every screen is one of the system's is held per screen by
    /// <see cref="ScreenAccessibilityTests"/>, under the same palette.
    /// </summary>
    [Fact]
    public async Task InHighContrastEveryColourIsTheSystems()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var findings = new List<string>();
        var real = ThemeManager.SystemHighContrast;
        ThemeManager.SystemHighContrast = () => true;
        try
        {
            await app.RunAsync(() =>
            {
                ThemeManager.Apply(AppTheme.Light, app.Resources);
                Assert.Equal(AppTheme.HighContrast, ThemeManager.Current);
                Assert.Equal(AppTheme.Light, ThemeManager.Requested);
                var palette = typeof(SystemColors).GetProperties().Where(p => p.PropertyType == typeof(Color)).Select(p => (Color)p.GetValue(null)!).ToHashSet();
                foreach (var key in app.Resources.MergedDictionaries[0].Keys.OfType<string>().Where(k => k.StartsWith("Brush.", StringComparison.Ordinal)))
                {
                    if (app.Resources[key] is not SolidColorBrush brush || !palette.Contains(brush.Color))
                    {
                        findings.Add($"{key} is not a system colour under high contrast");
                    }
                }
            });
        }
        finally
        {
            ThemeManager.SystemHighContrast = real;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }

        Assert.True(findings.Count == 0, string.Join('\n', findings.Distinct()));
    }
}
