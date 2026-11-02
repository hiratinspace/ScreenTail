using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Settings;
using ScreenTail.UI.Settings.Privacy;
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace ScreenTail.Tests.Windows.Settings;

/// <summary>
/// Settings → Privacy's retention slider over its view model (ST-081). The pane is kept and reloaded on
/// every visit, so one view model sees the admin's policy change. A real WPF slider clamps its value when
/// its top shrinks and pushes the clamped value back through the binding; this checks the screen never
/// shows, or saves, a value that came from that clamp. The slider is bound as PrivacyPane.xaml binds it.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class PrivacyViewModelTests(Accessibility.TheApplication app)
{
    [Fact]
    public async Task TheRetentionSliderFollowsThePolicyAsItChangesAndNeverSavesAClampedValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var own = new ClientSettings { LocalOnly = true, RetentionDays = 7 };
            var gateway = new FakePrivacy();
            var screen = new PrivacyViewModel(new PrivacyPanel(gateway), () => null, () => Task.CompletedTask);
            var slider = new Slider { Minimum = 1, TickFrequency = 1, IsSnapToTickEnabled = true };
            _ = slider.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(PrivacyViewModel.RetentionMaximum)) { Source = screen });
            _ = slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(PrivacyViewModel.RetentionDays)) { Source = screen });

            async Task Shows(int days, int maximum, bool locked, string policy)
            {
                gateway.Snapshot = new SettingsSnapshot(own, LocalOnlyLocked: false, RetentionLocked: locked, PolicyVersion: policy, EffectiveLocalOnly: true, EffectiveRetentionDays: locked ? days : own.RetentionDays);
                await screen.LoadAsync();
                Assert.Equal(days, screen.RetentionDays);
                Assert.Equal(days, (int)slider.Value);
                Assert.Equal(maximum, screen.RetentionMaximum);
                Assert.Equal(maximum, (int)slider.Maximum);
                Assert.Equal(!locked, screen.RetentionEditable);
            }

            await Shows(90, 90, locked: true, "p1");
            await Shows(3, ClientSettings.MaxRetentionDays, locked: true, "p2");
            await Shows(90, 90, locked: true, "p3");
            await Shows(7, ClientSettings.MaxRetentionDays, locked: false, "default");

            screen.Telemetry = true;
            await screen.SaveCommand.ExecuteAsync(null);
            Assert.Equal(7, gateway.Saved!.RetentionDays);
            Assert.Equal(7, (int)slider.Value);
        });
    }

    /// <summary>
    /// The first time the pane appears, through the real PrivacyPane.xaml. SettingsPane hands the pane its
    /// view model by a binding, so the pane is built before its view model arrives, and the view model may
    /// already be loaded by then. Until the slider's top is bound it is WPF's default of 10, so if the value
    /// were bound first a 12-day retention would be clamped to 10 and pushed back, and a save would write it.
    /// </summary>
    [Fact]
    public async Task TheFirstTimeThePaneAppearsItShowsAndSavesTheRetentionUnclamped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(FirstShowAsync);
    }

    private static async Task FirstShowAsync()
    {
        // The technician's own 12 days, unlocked: above the slider's default top.
        var own = new ClientSettings { LocalOnly = true, RetentionDays = 12 };
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(own, LocalOnlyLocked: false, RetentionLocked: false, PolicyVersion: "default", EffectiveLocalOnly: true, EffectiveRetentionDays: 12),
        };
        var screen = new PrivacyViewModel(new PrivacyPanel(gateway), () => null, () => Task.CompletedTask);
        await screen.LoadAsync();
        var slider = await ShownAsync(screen);
        Assert.Equal(ClientSettings.MaxRetentionDays, (int)slider.Maximum);
        Assert.Equal(12, (int)slider.Value);
        Assert.Equal(12, screen.RetentionDays);
        screen.Telemetry = true;
        await screen.SaveCommand.ExecuteAsync(null);
        Assert.Equal(12, gateway.Saved!.RetentionDays);

        // The admin's 90 days, locked: shown whole, and the technician's own 12 is what a save keeps.
        gateway.Snapshot = new SettingsSnapshot(own, LocalOnlyLocked: false, RetentionLocked: true, PolicyVersion: "p1", EffectiveLocalOnly: true, EffectiveRetentionDays: 90);
        var locked = new PrivacyViewModel(new PrivacyPanel(gateway), () => null, () => Task.CompletedTask);
        await locked.LoadAsync();
        slider = await ShownAsync(locked);
        Assert.Equal(90, (int)slider.Maximum);
        Assert.Equal(90, (int)slider.Value);
        Assert.Equal(90, locked.RetentionDays);
        Assert.False(slider.IsEnabled);
        locked.Telemetry = true;
        await locked.SaveCommand.ExecuteAsync(null);
        Assert.Equal(12, gateway.Saved!.RetentionDays);
    }

    /// <summary>
    /// Builds the real pane, then gives it the view model, in the order SettingsPane.xaml does. Bindings made
    /// while a pane loads its XAML attach later, when the dispatcher reaches data binding, in the order the
    /// XAML declares them; the app lets that happen before the pane is ever drawn, so this does too.
    /// </summary>
    private static async Task<Slider> ShownAsync(PrivacyViewModel screen)
    {
        var pane = new PrivacyPane();
        pane.DataContext = screen;
        await Dispatcher.Yield(DispatcherPriority.Background);
        return Find<Slider>(pane, "Retention in days") ?? throw new InvalidOperationException("The pane has no retention slider.");
    }

    private static T? Find<T>(DependencyObject parent, string name)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if ((child is T found && AutomationProperties.GetName(found) == name ? found : Find<T>(child, name)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private sealed class FakePrivacy : IPrivacyGateway
    {
        public SettingsSnapshot? Snapshot { get; set; }

        public ClientSettings? Saved { get; private set; }

        public Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default)
        {
            Saved = settings;
            return Task.FromResult<string?>(null);
        }

        public Task<AuditSaved> ExportAuditAsync(string destination, CancellationToken ct = default) =>
            throw new InvalidOperationException("not under test");
    }
}
