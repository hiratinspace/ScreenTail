using ScreenTail.Core.Capture;
using ScreenTail.Core.Input;
using ScreenTail.Core.Net;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>The service's side of Settings → Capture (ST-080): read with the tools and what the policy forces, save and apply, refuse what cannot be saved.</summary>
public sealed class CaptureSettingsCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadingReportsTheDocumentWithTheToolsDevicesAndTheForcedScope()
    {
        var commands = Commands(new TenantPolicy("p1", 7, false, false, true));

        var reported = Assert.IsType<CaptureSettingsReported>(await commands.ReplyToAsync(new GetCaptureSettingsCommand { RequestId = 1 }));

        Assert.Equal("rdp", Assert.Single(reported.Tools).Id);
        Assert.Equal(new MicrophoneRow("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true), Assert.Single(reported.Microphones));
        Assert.True(reported.PolicyForcesAllWindows);
        Assert.Equal(120, reported.DefaultGraceSeconds);
        Assert.True(reported.Settings.AutoStart);
    }

    [Fact]
    public async Task SavingWritesAppliesAndRefusesABadDocument()
    {
        var applied = new List<CaptureSettings>();
        var commands = Commands(TenantPolicy.Default, applied.Add);

        var ok = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 2, Settings = new CaptureSettings { AutoStart = false, GraceSeconds = 60 } });
        var bad = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 3, Settings = new CaptureSettings { Sensitivity = "extreme" } });

        Assert.True(ok!.Ok);
        Assert.False(bad!.Ok);
        Assert.Contains("Low, Medium or High", bad.Error, StringComparison.Ordinal);
        Assert.False(Assert.Single(applied).AutoStart);
        Assert.Equal(60, new CaptureSettingsStore(Path.Combine(_dir, "capture.json")).Load().GraceSeconds);
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 4 }));
    }

    [Fact]
    public async Task TwoSavesAtOnceLeaveTheFileAndWhatIsInForceTheSame()
    {
        // As for Settings → Privacy: the second save waits until the first has written and applied, so the
        // file and the scope in force end on the same document.
        CaptureSettings? inForce = null;
        Task<CommandResult?>? second = null;
        var calls = 0;
        CaptureSettingsCommands? commands = null;
        commands = Commands(TenantPolicy.Default, settings =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                second = Task.Run(() => commands!.HandleAsync(new SetCaptureSettingsCommand { RequestId = 7, Settings = new CaptureSettings { CaptureAllWindows = false } }), TestContext.Current.CancellationToken);
                _ = second.Wait(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            }

            inForce = settings;
        });

        var first = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 6, Settings = new CaptureSettings { CaptureAllWindows = true } }, TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        var then = await second;

        Assert.True(first!.Ok);
        Assert.True(then!.Ok);
        Assert.False(new CaptureSettingsStore(Path.Combine(_dir, "capture.json")).Load().CaptureAllWindows);
        Assert.False(inForce!.CaptureAllWindows);
    }

    [Fact]
    public async Task AFileWithNullMembersIsTheDefaults()
    {
        // A null is an absent value, and an absent value is the shipped default. The file is read at
        // every start, so one that throws there throws on every restart.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "capture.json");
        await File.WriteAllTextAsync(
            path,
            """{"disabled_tools":null,"hotkeys":null,"sensitivity":null,"speech_model":null,"grace_seconds":null,"auto_start":false}""",
            TestContext.Current.CancellationToken);

        var loaded = new CaptureSettingsStore(path).Load();

        Assert.Empty(loaded.DisabledTools);
        Assert.Empty(loaded.Hotkeys);
        Assert.Equal("medium", loaded.Sensitivity);
        Assert.Empty(loaded.Problems());
        Assert.Equal(HotkeyBindings.Defaults[HotkeyAction.StartCapture], loaded.ToBindings().For(HotkeyAction.StartCapture));
        Assert.Equal(new SceneSamplerOptions(), loaded.ToSceneOptions());

        // The members that were there are kept: a null beside them is not grounds to drop the file.
        Assert.False(loaded.AutoStart);

        var reported = Assert.IsType<CaptureSettingsReported>(await Commands(TenantPolicy.Default).ReplyToAsync(new GetCaptureSettingsCommand { RequestId = 1 }, TestContext.Current.CancellationToken));
        Assert.Empty(reported.Settings.Hotkeys);
    }

    [Fact]
    public async Task NullEntriesInAFileAreDroppedRatherThanKept()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "capture.json");
        await File.WriteAllTextAsync(
            path,
            """{"disabled_tools":[null,"rdp",""],"hotkeys":{"StartCapture":null,"MarkMoment":"Ctrl+Alt+K"}}""",
            TestContext.Current.CancellationToken);

        var loaded = new CaptureSettingsStore(path).Load();

        Assert.Equal(["rdp"], loaded.DisabledTools);
        Assert.Equal("Ctrl+Alt+K", Assert.Single(loaded.Hotkeys).Value);
        Assert.Empty(loaded.Problems());
    }

    [Fact]
    public async Task ASaveWithNullMembersIsSavedAsTheDefaults()
    {
        // The same document can arrive over the pipe from an older or broken peer. It is the defaults
        // there too, rather than an exception inside the save.
        var applied = new List<CaptureSettings>();
        var commands = Commands(TenantPolicy.Default, applied.Add);
        var sent = new CaptureSettings { AutoStart = false } with { DisabledTools = null!, Hotkeys = null!, Sensitivity = null! };

        var result = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 2, Settings = sent }, TestContext.Current.CancellationToken);

        Assert.True(result!.Ok);
        var inForce = Assert.Single(applied);
        Assert.Empty(inForce.DisabledTools);
        Assert.Empty(inForce.Hotkeys);
        Assert.Equal("medium", inForce.Sensitivity);
        Assert.False(new CaptureSettingsStore(Path.Combine(_dir, "capture.json")).Load().AutoStart);
    }

    [Fact]
    public async Task TheChosenMicrophoneIsKeptInTheDocumentAndAppliedOnSave()
    {
        // AC3: the choice goes into capture.json and into force, so the recorder opens it next time.
        const string headset = "{0.0.1.00000000}.{headset}";
        var applied = new List<CaptureSettings>();
        var commands = Commands(TenantPolicy.Default, applied.Add);

        var saved = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 2, Settings = new CaptureSettings { MicrophoneId = headset } }, TestContext.Current.CancellationToken);

        Assert.True(saved!.Ok);
        Assert.Equal(headset, Assert.Single(applied).MicrophoneId);
        Assert.Equal(headset, new CaptureSettingsStore(Path.Combine(_dir, "capture.json")).Load().MicrophoneId);
        var reported = Assert.IsType<CaptureSettingsReported>(await commands.ReplyToAsync(new GetCaptureSettingsCommand { RequestId = 3 }, TestContext.Current.CancellationToken));
        Assert.Equal(headset, reported.Settings.MicrophoneId);
        Assert.Contains("\"microphone_id\"", await File.ReadAllTextAsync(Path.Combine(_dir, "capture.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileFromBeforeTheChoiceAndABlankChoiceAreWindowsDefault()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "capture.json");
        await File.WriteAllTextAsync(path, """{"auto_start":false}""", TestContext.Current.CancellationToken);
        Assert.Null(new CaptureSettingsStore(path).Load().MicrophoneId);

        await File.WriteAllTextAsync(path, """{"microphone_id":"   "}""", TestContext.Current.CancellationToken);
        Assert.Null(new CaptureSettingsStore(path).Load().MicrophoneId);
    }

    [Fact]
    public async Task AnIdNoDeviceCouldHaveIsRefused()
    {
        var applied = new List<CaptureSettings>();
        var commands = Commands(TenantPolicy.Default, applied.Add);

        var control = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 2, Settings = new CaptureSettings { MicrophoneId = "{0.0.1}\n{x}" } }, TestContext.Current.CancellationToken);
        var huge = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 3, Settings = new CaptureSettings { MicrophoneId = new string('x', 513) } }, TestContext.Current.CancellationToken);

        Assert.False(control!.Ok);
        Assert.False(huge!.Ok);
        Assert.Contains("microphone", control.Error, StringComparison.Ordinal);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task TheLevelMeterIsAnsweredAndClosedOverTheCommands()
    {
        var microphone = new Speech.ScriptedMicrophone();
        await using var meter = new MicrophoneMeter(microphone, () => false);
        var commands = Commands(TenantPolicy.Default, meter: meter);

        var level = Assert.IsType<MicrophoneLevelReported>(await commands.ReplyToAsync(new GetMicrophoneLevelCommand { RequestId = 4, DeviceId = Speech.ScriptedMicrophone.HeadsetId }, TestContext.Current.CancellationToken));
        Assert.Equal(4, level.RequestId);
        Assert.Equal(MicrophoneLevelStates.Opening, level.State);
        Assert.Equal(Speech.ScriptedMicrophone.HeadsetId, level.DeviceId);
        Assert.True(meter.IsOpen);

        var stopped = await commands.HandleAsync(new StopMicrophoneLevelCommand { RequestId = 5 }, TestContext.Current.CancellationToken);

        Assert.True(stopped!.Ok);
        Assert.False(meter.IsOpen);
        Assert.Equal(0, microphone.Open);
    }

    [Fact]
    public async Task WithoutAMeterTheLevelIsRefusedAndNothingOpens()
    {
        var commands = Commands(TenantPolicy.Default);

        Assert.Null(await commands.ReplyToAsync(new GetMicrophoneLevelCommand { RequestId = 4 }, TestContext.Current.CancellationToken));
        var refused = await commands.HandleAsync(new GetMicrophoneLevelCommand { RequestId = 4 }, TestContext.Current.CancellationToken);
        var stopped = await commands.HandleAsync(new StopMicrophoneLevelCommand { RequestId = 5 }, TestContext.Current.CancellationToken);

        Assert.False(refused!.Ok);
        Assert.True(stopped!.Ok);
    }

    private CaptureSettingsCommands Commands(TenantPolicy policy, Action<CaptureSettings>? apply = null, MicrophoneMeter? meter = null) => new(
        new CaptureSettingsStore(Path.Combine(_dir, "capture.json")),
        () => [new ToolRow("rdp", "Remote Desktop")],
        () => [new MicrophoneDevice("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true)],
        () => policy,
        () => 120,
        apply ?? (_ => { }),
        meter);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
