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
    private static readonly string[] Moving =
    [
        "review", "review-timeline", "review-enlarged", "history", "settings", "onboarding-welcome", "onboarding-permissions", "diagnostics", "shortcut-sheet",
    ];

    /// <summary>
    /// With animations off in Windows, the app says reduce motion, its motion helper gives no duration,
    /// nothing on any screen animates while it opens, while the timeline opens and closes or while a combo
    /// box drops down, and no style or template anywhere declares a storyboard (which would run whatever
    /// the setting said).
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
                        findings.AddRange(Audit.Moving(window).Select(f => $"{screen}: {f}"));
                        findings.AddRange(Audit.Storyboards(app.Resources, window).Select(f => $"{screen}: {f}"));
                        if (screen.StartsWith("review", StringComparison.Ordinal) && screen != "review-enlarged")
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
    /// high-contrast palette and remembers their choice; every colour token then resolves to a system
    /// colour; and every opaque colour drawn on every screen is one of the system's.
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

            foreach (var screen in Screens.All)
            {
                await app.RunAsync(async () =>
                {
                    ThemeManager.Apply(AppTheme.Dark, app.Resources);
                    var window = await Screens.OpenAsync(screen);
                    try
                    {
                        findings.AddRange(Audit.OffPalette(window).Select(f => $"{screen}: {f}"));
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
            ThemeManager.SystemHighContrast = real;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }

        Assert.True(findings.Count == 0, string.Join('\n', findings.Distinct()));
    }
}
