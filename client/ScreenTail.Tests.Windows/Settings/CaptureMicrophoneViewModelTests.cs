using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;
using ScreenTail.UI.Settings.Capture;

namespace ScreenTail.Tests.Windows.Settings;

/// <summary>
/// Settings, Capture's microphone row over its view model, on a real dispatcher (ST-080 AC3): the picker
/// sets the device the meter listens to and the one saved, the level reaches the screen only while the
/// card is looked at, and a chosen device that is not connected is said on the card.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class CaptureMicrophoneViewModelTests
{
    private const string Headset = "{0.0.1.00000000}.{headset}";
    private const string BuiltIn = "{0.0.1.00000000}.{built-in}";

    [Fact]
    public void ThePickerChoosesTheDeviceTheMeterListensToAndTheOneSaved()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        OnUiThread(async () =>
        {
            var service = new FakeCapture();
            var screen = new CaptureViewModel(new CapturePanel(service), _ => Task.CompletedTask, new MicrophoneLevelWatch(service, ct => Task.Delay(10, ct)));
            await screen.LoadAsync();

            Assert.Equal([null, Headset, BuiltIn], screen.MicrophoneOptions.Select(o => o.Id));
            Assert.Null(screen.SelectedMicrophone!.Id);
            Assert.Equal(Visibility.Collapsed, screen.MicrophoneNoteVisibility);
            Assert.Equal(MicrophoneLevelWatch.NotWatching, screen.MicrophoneLevelStatus);
            Assert.Equal(0, service.Reads);

            screen.SelectedMicrophone = screen.MicrophoneOptions[1];
            await screen.SetMicrophoneCardWatchedAsync(true);
            await UntilAsync(() => screen.MicrophoneLevel > 0);

            Assert.Equal(40, screen.MicrophoneLevel, precision: 3);
            Assert.Equal(MicrophoneLevelWatch.WhileWatching, screen.MicrophoneLevelStatus);
            Assert.Equal(Headset, service.AskedFor[^1]);
            Assert.True(screen.HasUnsavedEdits);

            await screen.SetMicrophoneCardWatchedAsync(false);
            Assert.Equal(0, screen.MicrophoneLevel);
            Assert.Equal(MicrophoneLevelWatch.NotWatching, screen.MicrophoneLevelStatus);
            Assert.Equal(1, service.Stops);

            Assert.Null(await screen.Section.SaveAsync());
            screen.Refresh();
            Assert.Equal(Headset, service.Saved!.MicrophoneId);
        });
    }

    [Fact]
    public void AChosenDeviceThatIsNotConnectedIsSaidOnTheCard()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        OnUiThread(async () =>
        {
            var service = new FakeCapture { Own = new CaptureSettings { MicrophoneId = "{0.0.1.00000000}.{left-at-home}" } };
            var screen = new CaptureViewModel(new CapturePanel(service), _ => Task.CompletedTask, new MicrophoneLevelWatch(service));
            await screen.LoadAsync();

            Assert.Equal("{0.0.1.00000000}.{left-at-home}", screen.SelectedMicrophone!.Id);
            Assert.Equal(Visibility.Visible, screen.MicrophoneNoteVisibility);
            Assert.Contains("Built-in microphone", screen.MicrophoneNote, StringComparison.Ordinal);

            screen.SelectedMicrophone = screen.MicrophoneOptions[0];
            Assert.Equal(Visibility.Collapsed, screen.MicrophoneNoteVisibility);
        });
    }

    [Fact]
    public void TheSpeechModelAndSensitivityDropdownsAlwaysShowAChoiceByName()
    {
        // Spec v0.6.8: with the default settings the speech model dropdown was empty, and the sensitivity
        // showed its stored value ("medium"). Both show a named choice, and showing it is not a change.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        OnUiThread(async () =>
        {
            var service = new FakeCapture();
            var screen = new CaptureViewModel(new CapturePanel(service), _ => Task.CompletedTask, new MicrophoneLevelWatch(service));
            await screen.LoadAsync();

            Assert.NotNull(screen.SelectedModel);
            Assert.Same(screen.Models[0], screen.SelectedModel);
            Assert.Equal("Automatic", screen.SelectedModel.Label);
            Assert.Equal("Medium", screen.SelectedSensitivity.Label);
            Assert.Equal(["Low: fewer screenshots", "Medium", "High: more screenshots"], screen.Sensitivities.Select(s => s.Label));
            Assert.False(screen.HasUnsavedEdits);

            // The dropdowns write what they always wrote: the model's name, and the stored value.
            screen.SelectedModel = screen.Models.Single(m => m.Name == "small.en");
            screen.SelectedSensitivity = screen.Sensitivities.Single(s => s.Value == "high");
            Assert.Equal("high", screen.Sensitivity);
            Assert.True(screen.HasUnsavedEdits);
            Assert.Null(await screen.Section.SaveAsync());
            screen.Refresh();
            Assert.Equal("small.en", service.Saved!.SpeechModel);
            Assert.Equal("high", service.Saved.Sensitivity);
            Assert.Equal("small.en", screen.SelectedModel.Label);
            Assert.Equal("High: more screenshots", screen.SelectedSensitivity.Label);

            // A dropdown cleared (WPF hands a null) goes back to the default, never to nothing.
            screen.SelectedModel = null!;
            Assert.Same(screen.Models[0], screen.SelectedModel);
        });
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

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
        { IsBackground = true, Name = "capture microphone view model test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");
        failure?.Throw();
    }

    private sealed class FakeCapture : ICaptureGateway, IMicrophoneLevelGateway
    {
        private int _reads;
        private int _stops;

        public CaptureSettings Own { get; init; } = new();

        public CaptureSettings? Saved { get; private set; }

        public int Reads => Volatile.Read(ref _reads);

        public int Stops => Volatile.Read(ref _stops);

        public List<string?> AskedFor { get; } = [];

        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<CaptureSnapshot?>(new CaptureSnapshot(
                Own,
                [],
                [new MicrophoneDevice(Headset, "Headset", IsDefault: false), new MicrophoneDevice(BuiltIn, "Built-in microphone", IsDefault: true)],
                PolicyForcesAllWindows: false,
                DefaultGraceSeconds: 120));

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default)
        {
            Saved = settings;
            return Task.FromResult<string?>(null);
        }

        public Task<MeterReading?> ReadLevelAsync(string? deviceId, CancellationToken ct = default)
        {
            lock (AskedFor)
            {
                AskedFor.Add(deviceId);
            }

            _ = Interlocked.Increment(ref _reads);
            return Task.FromResult<MeterReading?>(new MeterReading(MicrophoneLevelStates.Listening, 0.4, deviceId));
        }

        public Task StopLevelAsync(CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }
    }
}
