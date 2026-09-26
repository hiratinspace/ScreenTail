using ScreenTail.Core.Input;
using ScreenTail.Core.Settings;
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
                ["Headset (Jabra)", "Built-in microphone"],
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

    private sealed class FakeCapture : ICaptureGateway
    {
        public CaptureSnapshot? Snapshot { get; set; } = new(new CaptureSettings(), [new ToolChoice("rdp", "Remote Desktop")], ["Built-in microphone"], false, 120);

        public CaptureSettings? Saved { get; private set; }

        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default)
        {
            Saved = settings;
            return Task.FromResult<string?>(null);
        }
    }
}
