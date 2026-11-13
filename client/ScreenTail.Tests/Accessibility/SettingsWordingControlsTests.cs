using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// Settings' wording as the XAML draws it (Spec v0.6.8, Settings): the two dropdowns show names, not the
/// stored values, and the speech model's is bound to a choice that is never missing; the Hudu hint is
/// under its box once. Read off the files, so it holds on the Mac. What the choices are is CapturePanel's.
/// </summary>
public sealed class SettingsWordingControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void TheSensitivityDropdownShowsEachChoicesNameNotItsStoredValue()
    {
        var capture = Load("Capture", "CapturePane.xaml");

        var box = Assert.Single(capture.Descendants(Wpf + "ComboBox"), c => (string?)c.Attribute("AutomationProperties.Name") == "Scene-change sensitivity");
        Assert.Equal("{Binding Sensitivities}", (string?)box.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedSensitivity}", (string?)box.Attribute("SelectedItem"));
        Assert.Equal("Label", (string?)box.Attribute("DisplayMemberPath"));

        // Each item is read out by its name too, not by its type.
        Assert.Contains(box.Descendants(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "AutomationProperties.Name" && (string?)s.Attribute("Value") == "{Binding Label}");
    }

    [Fact]
    public void TheSpeechModelDropdownIsBoundToTheChoiceThatIsNeverMissingAndShowsItsName()
    {
        var capture = Load("Capture", "CapturePane.xaml");

        var box = Assert.Single(capture.Descendants(Wpf + "ComboBox"), c => (string?)c.Attribute("AutomationProperties.Name") == "Speech model");
        Assert.Equal("{Binding Models}", (string?)box.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedModel}", (string?)box.Attribute("SelectedItem"));
        Assert.Contains(box.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding Label}");
        Assert.Contains(box.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding Hint}");
        Assert.Contains(box.Descendants(Wpf + "Binding"), b => (string?)b.Attribute("Path") == "Label");
        Assert.DoesNotContain(box.Descendants(Wpf + "Binding"), b => (string?)b.Attribute("Path") == "Name");
    }

    [Fact]
    public void TheHuduHintIsUnderItsBoxOnce()
    {
        var integrations = Load("Integrations", "IntegrationsPane.xaml");

        _ = Assert.Single(integrations.Descendants(Wpf + "TextBlock"), t => ((string?)t.Attribute("Text") ?? string.Empty).Contains("Hudu → Admin → API Keys", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Capture", "CapturePane.xaml")]
    [InlineData("Integrations", "IntegrationsPane.xaml")]
    public void NothingOnAPageSendsTheTechnicianToThePageTheyAreOn(string folder, string file)
    {
        var page = Load(folder, file);

        Assert.DoesNotContain(page.Descendants().Attributes(), a => a.Value.Contains("Settings → Integrations", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Descendants().Attributes(), a => a.Value.Contains('—', StringComparison.Ordinal));
    }

    private static XElement Load(string folder, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI", "Settings", folder, file);
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate).Root!;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(file);
    }
}
