using System.Runtime.Versioning;
using ScreenTail.Core.Shell;
using ScreenTail.UI.Shell;
using ScreenTail.UI.Theme;
using AppTheme = ScreenTail.UI.Theme.AppTheme;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Spec v0.6.0 §3 Frame (built v0.6.12): every window's title bar is tinted with the frame's colours,
/// follows the theme when it changes, and is the system's own in high contrast. DWM draws the title bar
/// outside WPF, so no render shows it; this holds what the shell asks DWM for, on a real window handle.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class TitleBarTintTests(TheApplication app)
{
    [Fact]
    public async Task TheShellsTitleBarIsTheFrameInEveryTheme()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var real = ThemeManager.SystemHighContrast;
        try
        {
            await app.RunAsync(async () =>
            {
                var shell = await Screens.OpenAsync("review");
                try
                {
                    Assert.True(TitleBar.GetTint(shell), "The shell is not built on Window.Base's title bar tint.");

                    ThemeManager.Apply(AppTheme.Dark, app.Resources);
                    await Audit.Settle();
                    Assert.Equal(TitleBarTint.For(false, "#121826", "#F7F5FE"), TitleBar.GetApplied(shell));

                    ThemeManager.Apply(AppTheme.Light, app.Resources);
                    await Audit.Settle();
                    Assert.Equal(TitleBarTint.For(false, "#141030", "#F7F5FE"), TitleBar.GetApplied(shell));

                    ThemeManager.SystemHighContrast = () => true;
                    ThemeManager.Apply(AppTheme.Light, app.Resources);
                    await Audit.Settle();
                    Assert.Equal(TitleBarTint.System, TitleBar.GetApplied(shell));
                }
                finally
                {
                    await Screens.CloseAsync(shell);
                }
            });
        }
        finally
        {
            ThemeManager.SystemHighContrast = real;
            await app.RunAsync(() => ThemeManager.Apply(AppTheme.Dark, app.Resources));
        }
    }

    [Fact]
    public async Task TheOneDialogTakesTheTintToo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var dialog = await Screens.ShowModalAsync(new ConfirmWindow(ConfirmCopy.QuitWhileCapturing));
            try
            {
                Assert.True(TitleBar.GetTint(dialog));
                Assert.Equal(TitleBarTint.For(false, "#121826", "#F7F5FE"), TitleBar.GetApplied(dialog));
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }
}
