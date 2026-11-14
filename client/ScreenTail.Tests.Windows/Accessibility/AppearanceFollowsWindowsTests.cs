using System.Runtime.Versioning;
using ScreenTail.Core.Shell;
using ScreenTail.UI.Theme;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Settings, Appearance (ST-070): System follows Windows' app mode, and changes when it does; Light and
/// Dark stay put; high contrast takes over from all three. Windows' app mode is simulated through
/// <see cref="ThemeManager"/>'s seam, as high contrast is, so the laptop's own setting is never changed.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class AppearanceFollowsWindowsTests(TheApplication app)
{
    [Fact]
    public async Task SystemFollowsWindowsAsItChanges()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var light = false;
        var real = ThemeManager.WindowsUsesLight;
        ThemeManager.WindowsUsesLight = () => light;
        try
        {
            await app.RunAsync(() =>
            {
                ThemeManager.Follow(ThemeChoice.System, app.Resources);
                Assert.Equal(AppTheme.Dark, ThemeManager.Current);

                light = true;
                ThemeManager.ModeChanged(app.Resources);
                Assert.Equal(AppTheme.Light, ThemeManager.Current);
            });
        }
        finally
        {
            ThemeManager.WindowsUsesLight = real;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }
    }

    [Theory]
    [InlineData(ThemeChoice.Light, AppTheme.Light)]
    [InlineData(ThemeChoice.Dark, AppTheme.Dark)]
    public async Task AChosenThemeStaysWhenWindowsChanges(ThemeChoice choice, AppTheme shown)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var light = shown == AppTheme.Dark;
        var real = ThemeManager.WindowsUsesLight;
        ThemeManager.WindowsUsesLight = () => light;
        try
        {
            await app.RunAsync(() =>
            {
                ThemeManager.Follow(choice, app.Resources);
                Assert.Equal(shown, ThemeManager.Current);

                light = !light;
                ThemeManager.ModeChanged(app.Resources);
                Assert.Equal(shown, ThemeManager.Current);
            });
        }
        finally
        {
            ThemeManager.WindowsUsesLight = real;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }
    }

    [Fact]
    public async Task AFixedThemeFromARenderIsNotMovedByWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var real = ThemeManager.WindowsUsesLight;
        ThemeManager.WindowsUsesLight = () => true;
        try
        {
            await app.RunAsync(() =>
            {
                ThemeManager.Follow(ThemeChoice.System, app.Resources);
                ThemeManager.Apply(AppTheme.Dark, app.Resources);

                ThemeManager.ModeChanged(app.Resources);

                Assert.Null(ThemeManager.Choice);
                Assert.Equal(AppTheme.Dark, ThemeManager.Current);
            });
        }
        finally
        {
            ThemeManager.WindowsUsesLight = real;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }
    }

    [Fact]
    public void ARadioHoldsTheChoiceForTheSaveBar()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var written = new List<ThemeChoice>();
        var screen = new UI.Settings.Appearance.AppearanceViewModel(
            new Core.Settings.AppearanceSection(ThemeChoice.System, c =>
            {
                written.Add(c);
                return true;
            }),
            () => "Dark");
        var edited = 0;
        screen.Edited += (_, _) => edited++;

        screen.Light = true;

        Assert.False(screen.UseWindows);
        Assert.Equal([Core.Settings.AppearanceSection.Theme], screen.Section.Changes);
        Assert.Equal(System.Windows.Visibility.Visible, screen.Changed["Theme"]);
        Assert.Equal("Windows is set to Dark now.", screen.WindowsNow);
        Assert.Equal(1, edited);
        Assert.Empty(written);
    }

    [Fact]
    public async Task HighContrastTakesOverFromEveryChoice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var realMode = ThemeManager.WindowsUsesLight;
        var realContrast = ThemeManager.SystemHighContrast;
        ThemeManager.WindowsUsesLight = () => true;
        ThemeManager.SystemHighContrast = () => true;
        try
        {
            await app.RunAsync(() =>
            {
                foreach (var choice in Enum.GetValues<ThemeChoice>())
                {
                    ThemeManager.Follow(choice, app.Resources);
                    Assert.Equal(AppTheme.HighContrast, ThemeManager.Current);
                    Assert.Equal(choice, ThemeManager.Choice);
                }
            });
        }
        finally
        {
            ThemeManager.WindowsUsesLight = realMode;
            ThemeManager.SystemHighContrast = realContrast;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }
    }
}
