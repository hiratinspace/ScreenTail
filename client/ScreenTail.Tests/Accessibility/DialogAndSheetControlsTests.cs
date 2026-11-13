using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// The one confirm dialog, the diagnostics panel and the shortcut sheet as their XAML draws them (Spec §3
/// Modal, §4, §5 S1; v0.6.11), read off the files so it holds on the Mac and on every commit. What they do
/// on screen is held on Windows (ScreenAccessibilityTests, ConfirmWindowTests); this holds that each
/// question goes through the one dialog, that its buttons are named by the words they show, and that the
/// three surfaces are drawn from the tokens.
/// </summary>
public sealed class DialogAndSheetControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryQuestionGoesThroughTheOneDialog()
    {
        // Review's typed dialog and the shell's were two windows that looked different and said "Confirm"
        // and "Discard this session?" on their buttons; the message boxes said OK. One dialog now.
        Assert.False(File.Exists(Source("Review", "ConfirmTypedDialog.xaml")), "Review's own typed dialog is back; use Shell/ConfirmWindow.");
        var shell = File.ReadAllText(Source("Shell", "LiveShell.cs"));
        Assert.DoesNotContain("MessageBox.", shell, StringComparison.Ordinal);
        Assert.Contains("ConfirmCopy.", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialogsButtonsAreNamedByTheirWordsAndEscCancels()
    {
        var dialog = Load("Shell", "ConfirmWindow.xaml");
        var buttons = dialog.Descendants(Wpf + "Button").ToList();

        Assert.Equal(2, buttons.Count);
        var cancel = Assert.Single(buttons, b => (string?)b.Attribute("IsCancel") == "True");
        Assert.Equal("{Binding Cancel}", (string?)cancel.Attribute("Content"));
        Assert.Equal("{Binding Cancel}", (string?)cancel.Attribute("AutomationProperties.Name"));
        var action = Assert.Single(buttons, b => b != cancel);
        Assert.Equal("{Binding Action}", (string?)action.Attribute("Content"));
        // A notice has no action: its hidden button is named "" rather than null, which WPF rejects as a binding error.
        Assert.Equal("{Binding Action, TargetNullValue=''}", (string?)action.Attribute("AutomationProperties.Name"));

        // The phrase box is the dialog's own and is named by what it asks for.
        var typed = Assert.Single(dialog.Descendants(Wpf + "TextBox"));
        Assert.Equal("Typed", (string?)typed.Attribute(X + "Name"));
        Assert.Equal("{StaticResource Input.Mono}", (string?)typed.Attribute("Style"));
        Assert.False(string.IsNullOrWhiteSpace((string?)typed.Attribute("AutomationProperties.Name")));
    }

    [Theory]
    [InlineData("Shell", "ConfirmWindow.xaml")]
    [InlineData("Shell", "ShortcutSheet.xaml")]
    [InlineData("Diagnostics", "DiagnosticsWindow.xaml")]
    public void TheSurfacesUseTheTokens(string folder, string file)
    {
        var root = Load(folder, file);

        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.StartsWith('#'));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Name.LocalName is "FontSize" or "Foreground" or "Background" or "BorderBrush" && !a.Value.StartsWith('{'));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.Contains('—', StringComparison.Ordinal));
    }

    [Fact]
    public void TheSheetDrawsKeysAsKeyCapsAndKeepsCloseOnTheScreen()
    {
        var sheet = Load("Shell", "ShortcutSheet.xaml");

        Assert.Contains(sheet.Descendants(Wpf + "Border"), b => (string?)b.Attribute("Style") == "{StaticResource KeyCap.Sheet}");
        Assert.Contains(sheet.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Style") == "{StaticResource Sheet.GroupHeading}");

        // Wide enough that most lines take one line, and inside a 1024 px screen with its frame.
        var width = double.Parse((string)sheet.Attribute("Width")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(width, 760, 1000);

        var close = Assert.Single(sheet.Descendants(Wpf + "Button"));
        Assert.Equal("Close", (string?)close.Attribute("Content"));
        Assert.Equal("True", (string?)close.Attribute("IsCancel"));
    }

    [Fact]
    public void TheDiagnosticsPanelClosesWithEscAndSaysItsStateWithAGlyph()
    {
        var panel = Load("Diagnostics", "DiagnosticsWindow.xaml");

        var close = Assert.Single(panel.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Content") == "Close");
        Assert.Equal("True", (string?)close.Attribute("IsCancel"));
        Assert.Contains(panel.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Content") == "Copy diagnostics");

        // Spec §5 S1 asks for a Local-only badge; Spec §1.1 for state as a word, an icon and a colour.
        Assert.Contains(panel.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource Badge}" && ((string?)e.Attribute("AutomationProperties.Name"))?.Contains("Local-only", StringComparison.Ordinal) == true);
        Assert.Contains(panel.Descendants(), e => e.Name.LocalName == "Glyph");

        // The copy's answer is announced where the button is.
        Assert.Contains(panel.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding Copied}");
    }

    private static string Source(string folder, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var ui = Path.Combine(directory.FullName, "client", "ScreenTail.UI");
            if (Directory.Exists(ui))
            {
                return Path.Combine(ui, folder, file);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("client/ScreenTail.UI");
    }

    private static XElement Load(string folder, string file) => XDocument.Load(Source(folder, file)).Root!;
}
