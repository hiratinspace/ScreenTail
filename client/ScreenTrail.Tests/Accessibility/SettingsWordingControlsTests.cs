using System.Xml.Linq;

namespace ScreenTrail.Tests.Accessibility;

/// <summary>
/// Settings' wording as the XAML draws it (Spec v0.6.8, Settings): the two dropdowns show names, not the
/// stored values, and the speech model's is bound to a choice that is never missing; the integration
/// cards are read only (Spec v0.6.15). Read off the files, so it holds on the Mac. What the choices are is CapturePanel's.
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

        // Spec v0.7.1: the closed box shows only the choice's plain name, as the sensitivity's does; what
        // the chosen line is goes in the helper line under it, and the items read out their name.
        Assert.Equal("Label", (string?)box.Attribute("DisplayMemberPath"));
        Assert.Empty(box.Descendants(Wpf + "TextBlock"));
        Assert.Contains(box.Descendants(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "AutomationProperties.Name" && (string?)s.Attribute("Value") == "{Binding Label}");
        Assert.DoesNotContain(box.Descendants(Wpf + "Binding"), b => (string?)b.Attribute("Path") == "Name");
        Assert.Contains(capture.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding ModelHint}");
    }

    [Fact]
    public void AnIntegrationCardWhoseLastCheckFailedShowsTheWarningBadgeNotConnected()
    {
        // Spec v0.7.1: Hudu showed a green Connected badge over a failed check. The card has two badges,
        // each shown only in its own state, and the failed one is the warning tint.
        var card = CardTemplate();
        var badges = card.Descendants(Wpf + "ContentControl").ToList();

        Assert.Contains(badges, b => (string?)b.Attribute("Content") == "Connected" && (string?)b.Attribute("Visibility") == "{Binding ConnectedBadgeVisibility}");
        Assert.Contains(badges, b => (string?)b.Attribute("Content") == "Check failed"
            && (string?)b.Attribute("Style") == "{StaticResource Badge.Unconfirmed}"
            && (string?)b.Attribute("Visibility") == "{Binding CheckFailedBadgeVisibility}");
    }

    [Fact]
    public void TheIntegrationCardHasNoFieldsAndSaysWhoManagesIt()
    {
        // Spec v0.6.15 S7: integration credentials can only be changed by the tenant owner, in the web
        // admin. The card template (the mapping card below it keeps its own box) has nothing to type into,
        // and says who manages the connection. (Was: the Hudu key's hint, under its box once.)
        var card = CardTemplate();

        Assert.Empty(card.Descendants(Wpf + "TextBox"));
        Assert.Empty(card.Descendants(Wpf + "PasswordBox"));
        Assert.Contains(card.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding OwnerLine}");
        Assert.DoesNotContain(card.Descendants(Wpf + "TextBlock"), t => ((string?)t.Attribute("Text") ?? string.Empty).Contains("API Keys", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("Change")]
    [InlineData("Remove")]
    public void TheIntegrationCardHasNoButtonThatChangesTheConnection(string content)
    {
        var card = CardTemplate();

        Assert.DoesNotContain(card.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Content") == content);
        Assert.Contains(card.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Content") == "Test connection");
    }

    [Fact]
    public void ThePaneHasNoPasswordBoxAnywhere()
    {
        // Nothing secret is typed on this page any more, so nothing secret can sit in a collapsed control.
        Assert.Empty(Load("Integrations", "IntegrationsPane.xaml").Descendants(Wpf + "PasswordBox"));
    }

    private static XElement CardTemplate() =>
        Assert.Single(
            Load("Integrations", "IntegrationsPane.xaml").Descendants(Wpf + "DataTemplate"),
            t => t.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "IntegrationCardTemplate"));

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
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTrail.UI", "Settings", folder, file);
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate).Root!;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(file);
    }
}
