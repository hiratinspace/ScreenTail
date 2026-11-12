using System.Xml.Linq;

namespace ScreenTail.Tests.Theme;

/// <summary>
/// ST-016, WCAG 1.4.11: ContrastTests holds the control-edge tokens to 3:1, and this holds the controls to
/// those tokens. A light-theme review measured the inputs' outlines at 1.51:1 to 1.67:1 against the card
/// they sit on (border.strong, a container colour), so a text box was close to invisible. Read off
/// Components.xaml, so it holds on the Mac.
/// </summary>
public sealed class ControlEdgeTokensTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<string, string> Fields => new()
    {
        { "Input.Default", "TextBox" },
        { "", "PasswordBox" },
        { "", "ComboBox" },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void AFieldIsOutlinedInTheControlEdgeColour(string key, string targetType)
    {
        Assert.Equal("{DynamicResource Brush.border.control}", Setter(Style(key, targetType), "BorderBrush"));
    }

    [Theory]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    public void AnUncheckedBoxOrRadioIsOutlinedInTextMuted(string targetType)
    {
        Assert.Equal("{DynamicResource Brush.text.muted}", Setter(Style("", targetType), "BorderBrush"));
    }

    [Fact]
    public void TheOffToggleDrawsItsTrackAndThumbInTextSecondary()
    {
        var template = Style("", "controls:ToggleSwitch");
        var track = Named(template, "Track");
        var thumb = Named(template, "Thumb");

        Assert.Equal("{DynamicResource Brush.text.secondary}", track.Attribute("BorderBrush")?.Value);
        Assert.Equal("{DynamicResource Brush.text.secondary}", thumb.Attribute("Fill")?.Value);
    }

    private static XElement Style(string key, string targetType)
    {
        var styles = Components().Root!.Elements().Where(e => e.Name.LocalName == "Style");
        return styles.Single(s =>
            s.Attribute("TargetType")?.Value == targetType
            && (s.Attribute(X + "Key")?.Value ?? "") == key);
    }

    private static string? Setter(XElement style, string property) =>
        style.Elements().Where(e => e.Name.LocalName == "Setter")
            .SingleOrDefault(s => s.Attribute("Property")?.Value == property)
            ?.Attribute("Value")?.Value;

    private static XElement Named(XElement root, string name) =>
        root.Descendants().Single(e => e.Attribute(X + "Name")?.Value == name);

    private static XDocument Components()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI", "Theme", "Components.xaml");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("client/ScreenTail.UI/Theme/Components.xaml was not found above the test output directory");
    }
}
