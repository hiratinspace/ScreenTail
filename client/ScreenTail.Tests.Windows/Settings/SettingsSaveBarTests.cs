using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenTail.Tests.Windows.Accessibility;
using ScreenTail.UI.Settings;

namespace ScreenTail.Tests.Windows.Settings;

/// <summary>
/// Settings' one Save bar on the real shell (Spec, Settings layout): it appears with a change and goes
/// with Save or Undo, Ctrl+S saves from anywhere on the page, Esc undoes only while focus is on the bar,
/// and the page's view ends above the bar, so a focused control is never under it.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class SettingsSaveBarTests(TheApplication app)
{
    [Fact]
    public async Task CtrlSSavesEveryChangedSectionAndTheBarGoes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("settings");
            try
            {
                var pane = Audit.Descendants(shell).OfType<SettingsPane>().Single();
                var model = Assert.IsType<SettingsViewModel>(pane.DataContext);
                Assert.Equal(Visibility.Collapsed, model.BarVisibility);

                var save = pane.InputBindings.OfType<KeyBinding>().SingleOrDefault(b => b.Gesture is KeyGesture { Key: Key.S, Modifiers: ModifierKeys.Control });
                Assert.True(save is not null, "Ctrl+S is not bound in Settings.");
                Assert.Same(model.SaveCommand, save.Command);

                model.Privacy.Telemetry = !model.Privacy.Telemetry;
                await Audit.Settle();
                Assert.Equal(Visibility.Visible, model.BarVisibility);
                Assert.Equal("1 change not saved:", model.Summary);
                Assert.Equal("Privacy", model.LinkSection);

                var box = Audit.Descendants(pane).OfType<TextBox>().First(t => t.IsVisible && t.IsEnabled);
                _ = box.Focus();
                await Audit.Settle();
                Assert.True(Keys.Press(shell, Key.S, ModifierKeys.Control), "Ctrl+S on the page was not handled.");
                for (var i = 0; i < 20 && model.BarVisibility == Visibility.Visible; i++)
                {
                    await Audit.Settle();
                }

                Assert.Equal(Visibility.Collapsed, model.BarVisibility);
                Assert.False(model.Page.HasPendingChanges);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task EscUndoesOnlyFromTheBar()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("settings");
            try
            {
                var pane = Audit.Descendants(shell).OfType<SettingsPane>().Single();
                var model = Assert.IsType<SettingsViewModel>(pane.DataContext);
                var telemetry = model.Privacy.Telemetry;
                model.Privacy.Telemetry = !telemetry;
                await Audit.Settle();

                // Esc in a field on the page leaves the change alone.
                var box = Audit.Descendants(pane).OfType<TextBox>().First(t => t.IsVisible && t.IsEnabled);
                _ = box.Focus();
                await Audit.Settle();
                _ = Keys.Press(shell, Key.Escape);
                await Audit.Settle();
                Assert.True(model.Page.HasPendingChanges, "Esc outside the bar threw the changes away.");

                // Esc on the bar is Undo changes.
                var undo = Audit.Descendants(pane).OfType<Button>().Single(b => b.Content as string == "Undo changes");
                _ = undo.Focus();
                await Audit.Settle();
                Assert.True(Keys.Press(shell, Key.Escape), "Esc on the bar was not handled.");
                await Audit.Settle();
                Assert.False(model.Page.HasPendingChanges);
                Assert.Equal(telemetry, model.Privacy.Telemetry);
                Assert.Equal(Visibility.Collapsed, model.BarVisibility);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task TheBarNeverLiesOverThePage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("settings-pending");
            try
            {
                var pane = Audit.Descendants(shell).OfType<SettingsPane>().Single();
                var scroller = Audit.Descendants(pane).OfType<ScrollViewer>().First(s => s.Name == "Scroller");
                var bar = Audit.Descendants(pane).OfType<Border>().Single(b => b.Name == "Bar");
                var scrollerBottom = scroller.TranslatePoint(new Point(0, scroller.ActualHeight), shell).Y;
                var barTop = bar.TranslatePoint(new Point(0, 0), shell).Y;
                Assert.True(scrollerBottom <= barTop + 0.5, $"The page's view ends at {scrollerBottom}, under the bar at {barTop}.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }
}
