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

    /// <summary>
    /// The note's in-place fields (Spec v0.6.8 §5 S3): a faint edge at rest, so a field can be told from a
    /// heading before the pointer is over it. border.strong, the quiet edge the secondary buttons carry:
    /// seen on the pane in both themes, and well under the 3:1 control edge it becomes on hover, so the
    /// note still reads as a note. Focus keeps the 2 px accent outline.
    /// </summary>
    [Fact]
    public void TheNotesInPlaceFieldHasAFaintEdgeAtRestAndKeepsItsHoverAndFocusEdges()
    {
        var style = Style("Input.InPlace", "TextBox");

        Assert.Equal("{DynamicResource Brush.border.strong}", Setter(style, "BorderBrush"));

        // One pixel, from Input.Default: nothing here takes the edge away again.
        Assert.Null(Setter(style, "BorderThickness"));
        Assert.Equal("1", Setter(Style("Input.Default", "TextBox"), "BorderThickness"));

        var hover = Trigger(style, "IsMouseOver");
        Assert.Contains(hover, s => s.Attribute("Property")?.Value == "BorderBrush" && s.Attribute("Value")?.Value == "{DynamicResource Brush.border.control}");
        var focus = Trigger(style, "IsKeyboardFocused");
        Assert.Contains(focus, s => s.Attribute("Property")?.Value == "BorderBrush" && s.Attribute("Value")?.Value == "{DynamicResource Brush.accent.primary}");
        Assert.Contains(focus, s => s.Attribute("Property")?.Value == "BorderThickness" && s.Attribute("Value")?.Value == "2");
    }

    [Theory]
    [InlineData("Tokens.Light.g.xaml")]
    [InlineData("Tokens.Dark.g.xaml")]
    public void TheFaintEdgeShowsOnThePaneAndIsQuieterThanTheControlEdge(string tokens)
    {
        var pane = Colour(tokens, "bg.surface");
        var faint = ContrastTests.ContrastRatio(Colour(tokens, "border.strong"), pane);
        var divider = ContrastTests.ContrastRatio(Colour(tokens, "border.subtle"), pane);
        var control = ContrastTests.ContrastRatio(Colour(tokens, "border.control"), pane);

        // Seen: clearer than the pane's own dividers, and at least 1.5:1. Quiet: under the control edge,
        // which is the one held to 3:1 and is what hover shows.
        Assert.True(faint >= 1.5, $"border.strong is {faint:F2}:1 on bg.surface in {tokens}");
        Assert.True(faint > divider, $"border.strong ({faint:F2}:1) is no clearer than border.subtle ({divider:F2}:1) in {tokens}");
        Assert.True(faint < control, $"border.strong ({faint:F2}:1) is not quieter than border.control ({control:F2}:1) in {tokens}");
    }

    [Fact]
    public void InHighContrastTheFaintEdgeIsASystemColour()
    {
        var brush = Load("Tokens.HighContrast.xaml").Root!.Elements()
            .Single(e => e.Attribute(X + "Key")?.Value == "Brush.border.strong");

        Assert.StartsWith("{DynamicResource {x:Static SystemColors.", brush.Attribute("Color")?.Value, StringComparison.Ordinal);
    }

    private static IEnumerable<XElement> Trigger(XElement style, string property) =>
        style.Descendants().Single(e => e.Name.LocalName == "Trigger" && e.Attribute("Property")?.Value == property).Elements();

    private static string Colour(string tokens, string token) =>
        Load(tokens).Root!.Elements().Single(e => e.Name.LocalName == "Color" && e.Attribute(X + "Key")?.Value == "Token." + token).Value;

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

    private static XDocument Components() => Load("Components.xaml");

    private static XDocument Load(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI", "Theme", file);
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"client/ScreenTail.UI/Theme/{file} was not found above the test output directory");
    }
}
