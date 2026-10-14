using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Settings;
using ScreenTail.UI.Settings.Privacy;

namespace ScreenTail.Tests.Windows.Settings;

/// <summary>
/// Settings → Privacy's retention slider over its view model (ST-081). The pane is kept and reloaded on
/// every visit, so one view model sees the admin's policy change. A real WPF slider clamps its value when
/// its top shrinks and pushes the clamped value back through the binding; this checks the screen never
/// shows, or saves, a value that came from that clamp. The slider is bound as PrivacyPane.xaml binds it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrivacyViewModelTests
{
    [Fact]
    public void TheRetentionSliderFollowsThePolicyAsItChangesAndNeverSavesAClampedValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        OnUiThread(async () =>
        {
            var own = new ClientSettings { LocalOnly = true, RetentionDays = 7 };
            var gateway = new FakePrivacy();
            var screen = new PrivacyViewModel(new PrivacyPanel(gateway), _ => Task.CompletedTask, () => Task.CompletedTask);
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

    /// <summary>Runs the body on an STA thread with a dispatcher, as WPF controls and bindings require.</summary>
    private static void OnUiThread(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            var task = body();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            if (task.Exception is { } thrown)
            {
                failure = ExceptionDispatchInfo.Capture(thrown.InnerException ?? thrown);
            }

            dispatcher.InvokeShutdown();
        })
        { IsBackground = true, Name = "privacy view model test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");
        failure?.Throw();
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

        public Task<string?> ExportAuditAsync(CancellationToken ct = default) => Task.FromResult<string?>("{}");
    }
}
