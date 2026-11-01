using ScreenTail.Core.Input;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings → Capture's rules (ST-080, Spec §5 S5): the tools with a toggle each, the scope with its
/// warning and a confirmation before "All windows" saves (INV-5), auto-start and the grace period,
/// hotkeys checked for shape and clashes with an alternative offered (ST-029), scene-change sensitivity,
/// the microphone and the speech model, start at login. The service is a fake here.
/// </summary>
public sealed class CapturePanelTests
{
    [Fact]
    public async Task LoadListsTheToolsWithTheirTogglesAndTheMicrophones()
    {
        var gateway = new FakeCapture
        {
            Snapshot = new CaptureSnapshot(
                new CaptureSettings { DisabledTools = ["screenconnect"] },
                [new ToolChoice("screenconnect", "ScreenConnect"), new ToolChoice("rdp", "Remote Desktop")],
                [new MicrophoneDevice("{0.0.1.00000000}.{jabra}", "Headset (Jabra)", IsDefault: false), new MicrophoneDevice("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true)],
                PolicyForcesAllWindows: false,
                DefaultGraceSeconds: 120),
        };
        var panel = new CapturePanel(gateway);

        await panel.LoadAsync();

        Assert.Equal([false, true], panel.Tools.Select(t => t.Enabled));
        Assert.Equal(2, panel.Microphones.Count);
        Assert.Equal(120, panel.GraceSeconds);
        Assert.Equal(3, panel.Models.Count);
    }

    [Fact]
    public async Task AllWindowsWarnsAndIsNotSavedUntilConfirmed()
    {
        var gateway = new FakeCapture();
        var panel = new CapturePanel(gateway);
        await panel.LoadAsync();

        panel.CaptureAllWindows = true;

        Assert.Contains("other customers", panel.ScopeWarning, StringComparison.OrdinalIgnoreCase);
        Assert.False(panel.CanSave);
        panel.ConfirmAllWindows();
        Assert.True(panel.CanSave);
        Assert.True(await panel.SaveAsync());
        Assert.True(gateway.Saved!.CaptureAllWindows);

        panel.CaptureAllWindows = false;
        Assert.Null(panel.ScopeWarning);
        Assert.True(panel.CanSave);
    }

    [Fact]
    public async Task AHotkeyClashIsShownInlineWithAnAlternative()
    {
        var panel = new CapturePanel(new FakeCapture());
        await panel.LoadAsync();

        panel.SetHotkey(HotkeyAction.MarkMoment, "Ctrl+Alt+S");

        var problem = panel.HotkeyProblem(HotkeyAction.MarkMoment);
        Assert.NotNull(problem);
        Assert.Contains("Try ", problem, StringComparison.Ordinal);
        Assert.False(panel.CanSave);

        panel.SetHotkey(HotkeyAction.MarkMoment, "Ctrl+Alt+F9");
        Assert.Null(panel.HotkeyProblem(HotkeyAction.MarkMoment));
        Assert.True(panel.CanSave);
    }

    [Fact]
    public async Task TheMicrophoneAndModelApplyAtTheNextStartAndTheScreenSaysSo()
    {
        var gateway = new FakeCapture();
        var panel = new CapturePanel(gateway);
        await panel.LoadAsync();

        panel.Model = "small.en";
        Assert.Contains("next start", panel.AppliesAtNextStart, StringComparison.Ordinal);
        Assert.True(await panel.SaveAsync());

        Assert.Equal("small.en", gateway.Saved!.SpeechModel);
        Assert.Contains("accuracy", panel.Models.Single(m => m.Name == "small.en").Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATogglePersistsAsTheDisabledList()
    {
        var gateway = new FakeCapture
        {
            Snapshot = new CaptureSnapshot(new CaptureSettings(), [new ToolChoice("screenconnect", "ScreenConnect"), new ToolChoice("rdp", "Remote Desktop")], [], false, 120),
        };
        var panel = new CapturePanel(gateway);
        await panel.LoadAsync();

        panel.SetTool("rdp", false);
        panel.AutoStart = false;
        panel.GraceSeconds = 60;
        Assert.True(await panel.SaveAsync());

        Assert.Equal(["rdp"], gateway.Saved!.DisabledTools);
        Assert.False(gateway.Saved.AutoStart);
        Assert.Equal(60, gateway.Saved.GraceSeconds);
    }

    [Fact]
    public async Task WhenThePolicyForcesTheScopeItIsReadOnly()
    {
        var panel = new CapturePanel(new FakeCapture { Snapshot = new CaptureSnapshot(new CaptureSettings(), [], [], PolicyForcesAllWindows: true, 120) });
        await panel.LoadAsync();

        Assert.True(panel.CaptureAllWindows);
        Assert.True(panel.ScopeLocked);
    }

    [Fact]
    public async Task UnsavedEditsAreThoseThatDifferFromWhatWasLoadedOrSaved()
    {
        // The shell reads Settings again when the service comes back, and must not overwrite what the
        // technician changed and has not saved. This is how it knows.
        var gateway = new FakeCapture
        {
            Snapshot = new CaptureSnapshot(new CaptureSettings { GraceSeconds = 90, Hotkeys = new Dictionary<string, string> { ["MarkMoment"] = "Ctrl+Alt+B" } }, [new ToolChoice("rdp", "Remote Desktop")], [], false, 120),
        };
        var panel = new CapturePanel(gateway);
        Assert.False(panel.HasUnsavedEdits);
        await panel.LoadAsync();
        Assert.False(panel.HasUnsavedEdits);

        panel.GraceSeconds = 60;
        Assert.True(panel.HasUnsavedEdits);
        panel.GraceSeconds = 90;
        Assert.False(panel.HasUnsavedEdits);

        panel.SetTool("rdp", enabled: false);
        Assert.True(panel.HasUnsavedEdits);
        panel.SetTool("rdp", enabled: true);
        panel.SetHotkey(HotkeyAction.MarkMoment, "Ctrl+Alt+Q");
        Assert.True(panel.HasUnsavedEdits);
        panel.SetHotkey(HotkeyAction.MarkMoment, "Ctrl+Alt+B");
        Assert.False(panel.HasUnsavedEdits);

        panel.CaptureAllWindows = true;
        Assert.True(panel.HasUnsavedEdits);
        panel.ConfirmAllWindows();
        Assert.True(await panel.SaveAsync());
        Assert.False(panel.HasUnsavedEdits);
    }

    [Fact]
    public void AChangeMadeBeforeAnyLoadIsAnUnsavedEdit()
    {
        var panel = new CapturePanel(new FakeCapture());

        panel.Sensitivity = "high";

        Assert.True(panel.HasUnsavedEdits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AForcedScopeIsNotSavedAsTheTechniciansOwnChoice(bool ownChoice)
    {
        // The policy shows "All windows" on a read-only radio, and the technician saves something else on
        // the panel. What is saved is the scope they chose themselves, never the forced one: saved as
        // theirs, all-window capture would outlive the policy that forced it with no confirmation ever
        // given (INV-5).
        var gateway = new FakeCapture
        {
            Snapshot = new CaptureSnapshot(new CaptureSettings { CaptureAllWindows = ownChoice }, [], [], PolicyForcesAllWindows: true, 120),
        };
        var panel = new CapturePanel(gateway);
        await panel.LoadAsync();
        Assert.True(panel.CaptureAllWindows);

        panel.AutoStart = false;
        panel.CaptureAllWindows = !ownChoice;
        Assert.True(panel.CanSave);
        Assert.True(await panel.SaveAsync());

        Assert.NotNull(gateway.Saved);
        Assert.Equal(ownChoice, gateway.Saved.CaptureAllWindows);
        Assert.False(gateway.Saved.AutoStart);

        // The admin lifts the policy. The panel shows the technician's own scope, and all windows only if
        // they had confirmed it themselves.
        gateway.Snapshot = new CaptureSnapshot(gateway.Saved, [], [], PolicyForcesAllWindows: false, 120);
        await panel.LoadAsync();
        Assert.False(panel.ScopeLocked);
        Assert.Equal(ownChoice, panel.CaptureAllWindows);
    }

    [Fact]
    public async Task TheChosenMicrophoneIsSavedByItsIdAndReadBack()
    {
        var gateway = new FakeCapture { Snapshot = WithDevices(new CaptureSettings()) };
        var panel = new CapturePanel(gateway);
        await panel.LoadAsync();

        Assert.Null(panel.MicrophoneId);
        Assert.Equal(new MicrophoneOption(null, "Windows default (Built-in microphone)"), panel.MicrophoneOptions[0]);
        Assert.Equal([null, Jabra, BuiltIn], panel.MicrophoneOptions.Select(o => o.Id));
        Assert.Null(panel.MicrophoneNote);
        Assert.False(panel.HasUnsavedEdits);

        panel.MicrophoneId = Jabra;
        Assert.True(panel.HasUnsavedEdits);
        Assert.Equal("Headset (Jabra)", panel.SelectedMicrophone.Label);
        Assert.True(await panel.SaveAsync());

        Assert.Equal(Jabra, gateway.Saved!.MicrophoneId);
        gateway.Snapshot = WithDevices(gateway.Saved);
        await panel.LoadAsync();
        Assert.Equal(Jabra, panel.MicrophoneId);
        Assert.False(panel.HasUnsavedEdits);
    }

    [Fact]
    public async Task AChosenMicrophoneThatIsNotConnectedFallsBackToTheDefaultAndSaysSo()
    {
        // The headset was left at home. The card says which device is used instead and that sessions
        // start anyway, and the choice stays on the list so that saving a hotkey does not forget it.
        const string gone = "{0.0.1.00000000}.{left-at-home}";
        var gateway = new FakeCapture { Snapshot = WithDevices(new CaptureSettings { MicrophoneId = gone }) };
        var panel = new CapturePanel(gateway);
        await panel.LoadAsync();

        Assert.Equal(gone, panel.MicrophoneId);
        Assert.Equal(gone, panel.SelectedMicrophone.Id);
        Assert.Contains("not connected", panel.SelectedMicrophone.Label, StringComparison.Ordinal);
        Assert.Contains("not connected", panel.MicrophoneNote, StringComparison.Ordinal);
        Assert.Contains("Built-in microphone", panel.MicrophoneNote, StringComparison.Ordinal);
        Assert.Contains("Sessions start", panel.MicrophoneNote, StringComparison.Ordinal);

        panel.Sensitivity = "high";
        Assert.True(await panel.SaveAsync());
        Assert.Equal(gone, gateway.Saved!.MicrophoneId);

        panel.MicrophoneId = null;
        Assert.Null(panel.MicrophoneNote);
    }

    [Fact]
    public async Task WithNoMicrophoneTheCardSaysSessionsStillRecord()
    {
        var panel = new CapturePanel(new FakeCapture { Snapshot = new CaptureSnapshot(new CaptureSettings(), [], [], false, 120) });
        await panel.LoadAsync();

        Assert.Equal("Windows default (none connected)", Assert.Single(panel.MicrophoneOptions).Label);
        Assert.Contains("still record clicks and screenshots", panel.MicrophoneNote, StringComparison.Ordinal);
        Assert.True(panel.CanSave);
    }

    private const string Jabra = "{0.0.1.00000000}.{jabra}";
    private const string BuiltIn = "{0.0.1.00000000}.{built-in}";

    private static CaptureSnapshot WithDevices(CaptureSettings settings) => new(
        settings,
        [],
        [new MicrophoneDevice(Jabra, "Headset (Jabra)", IsDefault: false), new MicrophoneDevice(BuiltIn, "Built-in microphone", IsDefault: true)],
        false,
        120);

    private sealed class FakeCapture : ICaptureGateway
    {
        public CaptureSnapshot? Snapshot { get; set; } = new(new CaptureSettings(), [new ToolChoice("rdp", "Remote Desktop")], [new MicrophoneDevice("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true)], false, 120);

        public CaptureSettings? Saved { get; private set; }

        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default)
        {
            Saved = settings;
            return Task.FromResult<string?>(null);
        }
    }
}
