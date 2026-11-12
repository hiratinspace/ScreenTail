using System.Text.Json;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// Names a technician reads instead of ids (Spec §4, plain language): History, the shell's status line and
/// the tray said <c>screenconnect</c> and <c>rdp</c>, and the pill said "Not capturing: OUTLOOK".
/// </summary>
public sealed class DisplayNamesTests
{
    [Theory]
    [InlineData("screenconnect", "ScreenConnect")]
    [InlineData("rdp", "Remote Desktop")]
    [InlineData("anydesk", "AnyDesk")]
    [InlineData("teamviewer", "TeamViewer")]
    [InlineData("splashtop", "Splashtop")]
    [InlineData("browser", "Browser")]
    [InlineData("other", "Other window")]
    public void KnownToolsAreShownByTheirNames(string tool, string expected)
    {
        Assert.Equal(expected, ToolNames.Display(tool));
    }

    [Fact]
    public void EveryToolInTheRegistryIsShownByTheNameTheRegistryGivesIt()
    {
        // One list of names: the registry's. A tool added there without a name here would show as an id.
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));
        var tools = registry.RootElement.GetProperty("tools").EnumerateArray().ToList();
        Assert.NotEmpty(tools);
        Assert.All(tools, tool =>
        {
            var name = tool.GetProperty("display_name").GetString();
            Assert.Equal(name, ToolNames.Display(tool.GetProperty("kind").GetString()));
            Assert.Equal(name, ToolNames.Display(tool.GetProperty("id").GetString()));
        });
    }

    [Theory]
    [InlineData("vnc-viewer", "Vnc Viewer")]
    [InlineData("MYTOOL", "Mytool")]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    public void AToolThisVersionDoesNotKnowIsTitleCased(string? tool, string expected)
    {
        Assert.Equal(expected, ToolNames.Display(tool));
    }

    [Theory]
    [InlineData("OUTLOOK", "Outlook")]
    [InlineData("outlook.exe", "Outlook")]
    [InlineData("WINWORD", "Word")]
    [InlineData("msedge", "Microsoft Edge")]
    [InlineData("notepad", "Notepad")]
    [InlineData("SRManager", "SRManager")]
    [InlineData("my_tool", "My Tool")]
    public void AProcessIsNamedTheWayItsProductIs(string process, string expected)
    {
        // Mixed case is the product's own spelling and is kept; all upper or all lower is a file name.
        Assert.Equal(expected, ProcessNames.Friendly(process));
    }

    [Fact]
    public void ThePillNamesTheWindowInFrontInAFriendlyForm()
    {
        // INV-10: still the process and nothing else; only its spelling changes.
        var hud = HudState.For(new CaptureStateSnapshot { State = CaptureStates.Suppressed, SessionId = "s1", Reason = CaptureReasons.OutOfScope, ScopeProcess = "OUTLOOK" });

        Assert.Equal(HudTone.Scope, hud.State.Tone);
        Assert.Equal("Not capturing: Outlook", hud.State.Text);
    }

    [Fact]
    public void TheStatusLineAndTheTrayNameTheTool()
    {
        var capture = new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", RemoteTool = "rdp", ElapsedMs = 12 * 60 * 1000 };
        var snapshot = new ShellSnapshot(ServiceConnection.Connected, capture, ShellView.Review, Banner: null);

        Assert.Equal("Recording: Remote Desktop (12:00)", ShellStatus.Describe(snapshot));
        Assert.Equal("ScreenTail · Recording: Remote Desktop (12:00)", TrayPresence.From(snapshot).Tooltip);
    }
}
