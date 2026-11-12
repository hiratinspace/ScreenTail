using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// The workspace frame and the inbox rail as the XAML draws them (Spec v0.6.0 §5 S4, as built v0.6.4),
/// read off the files so the keys, the names and the frame's tokens hold on the Mac and on every commit.
/// What the rail lists is <c>InboxRail</c>'s and the view model's; this holds that the window reaches them.
/// </summary>
public sealed class InboxRailControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CtrlFReachesTheSearchAndEscClearsIt()
    {
        var shell = Load("Shell", "ShellWindow.xaml");
        var find = Assert.Single(shell.Descendants(Wpf + "KeyBinding"), k => (string?)k.Attribute("Key") == "F");
        Assert.Equal("Ctrl", (string?)find.Attribute("Modifiers"));
        Assert.Equal("{x:Static shell:ShellWindow.FindCommand}", (string?)find.Attribute("Command"));

        var rail = Load("History", "InboxRailView.xaml");
        var search = Assert.Single(rail.Descendants(Wpf + "TextBox"));
        Assert.Equal("Search", (string?)search.Attribute(Xaml + "Name"));
        Assert.Equal("Search sessions", (string?)search.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding Query, UpdateSourceTrigger=PropertyChanged}", (string?)search.Attribute("Text"));
        var esc = Assert.Single(search.Descendants(Wpf + "KeyBinding"));
        Assert.Equal("Escape", (string?)esc.Attribute("Key"));
        Assert.Equal("{Binding ClearSearchCommand}", (string?)esc.Attribute("Command"));
    }

    [Fact]
    public void EachGroupIsAListNamedWithItsCountAndEachRowIsSpoken()
    {
        var rail = Load("History", "InboxRailView.xaml");
        var list = Assert.Single(rail.Descendants(Wpf + "ListBox"));
        Assert.Equal("{Binding Heading}", (string?)list.Attribute("AutomationProperties.Name"));
        Assert.Equal("{StaticResource Rail.Row}", (string?)list.Attribute("ItemContainerStyle"));

        // Earlier opens and closes from a named toggle.
        var toggle = Assert.Single(rail.Descendants(Wpf + "ToggleButton"));
        Assert.Equal("{Binding IsExpanded}", (string?)toggle.Attribute("IsChecked"));
        Assert.Equal("{Binding Heading}", (string?)toggle.Attribute("AutomationProperties.Name"));

        var row = Style("Rail.Row");
        Assert.Equal("{Binding Spoken}", Setter(row, "AutomationProperties.Name"));
        Assert.Equal("{Binding IsOpen, Mode=OneWay}", Setter(row, "IsSelected"));
        Assert.Equal("{StaticResource FocusRing.Rail}", Setter(row, "FocusVisualStyle"));
    }

    [Fact]
    public void TheOpenRowIsThePanesTabWithTheRibbonBand()
    {
        var row = Style("Rail.Row");
        var band = Assert.Single(row.Descendants(Wpf + "Border"), b => (string?)b.Attribute(Xaml + "Name") == "Band");
        Assert.Equal("4", (string?)band.Attribute("Height"));
        Assert.Equal("{DynamicResource Brush.brand.ribbon.band}", (string?)band.Attribute("Background"));

        var open = Assert.Single(row.Descendants(Wpf + "DataTrigger"), t => (string?)t.Attribute("Binding") == "{Binding IsOpen}");
        var setters = open.Elements(Wpf + "Setter").ToList();
        Assert.Contains(setters, s => (string?)s.Attribute("TargetName") == "Band" && (string?)s.Attribute("Value") == "Visible");

        // The pane's own background, so the row joins it, and the Highlight edge that stays in high contrast.
        Assert.Contains(setters, s => (string?)s.Attribute("TargetName") == "Chrome" && (string?)s.Attribute("Property") == "Background" && (string?)s.Attribute("Value") == "{DynamicResource Brush.bg.base}");
        Assert.Contains(setters, s => (string?)s.Attribute("TargetName") == "Chrome" && (string?)s.Attribute("Property") == "BorderBrush" && (string?)s.Attribute("Value") == "{DynamicResource Brush.shell.accent}");
    }

    [Fact]
    public void FocusOnTheFrameIsAThreePixelShellAccentRing()
    {
        var ring = Assert.Single(Style("FocusRing.Rail").Descendants(Wpf + "Border"));
        Assert.Equal("3", (string?)ring.Attribute("BorderThickness"));
        Assert.Equal("{DynamicResource Brush.shell.accent}", (string?)ring.Attribute("BorderBrush"));

        foreach (var key in new[] { "Rail.Search", "Rail.GroupToggle", "Rail.FootItem", "Rail.Link" })
        {
            Assert.Equal("{StaticResource FocusRing.Rail}", Setter(Style(key), "FocusVisualStyle"));
        }
    }

    [Fact]
    public void TheRailsFootHasAllSessionsSettingsAndDiagnostics()
    {
        var shell = Load("Shell", "ShellWindow.xaml");
        var named = shell.Descendants(Wpf + "Button").Select(b => (string?)b.Attribute("AutomationProperties.Name")).ToList();
        Assert.Contains("All sessions", named);
        Assert.Contains("Settings", named);
        Assert.Contains("Diagnostics", named);

        var diagnostics = Assert.Single(shell.Descendants(Wpf + "Button"), b => (string?)b.Attribute("AutomationProperties.Name") == "Diagnostics");
        Assert.Equal("{Binding OpenDiagnosticsCommand}", (string?)diagnostics.Attribute("Command"));

        // The v0.5 header tabs are gone: the rail is History, the pane is Review, Settings is in the foot.
        Assert.DoesNotContain(shell.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Content") is "Review" or "History");
    }

    [Fact]
    public void TheFrameIsShellBgWithTheSessionPaneOnBgBase()
    {
        var shell = Load("Shell", "ShellWindow.xaml");
        var grid = shell.Elements(Wpf + "Grid").Single();
        Assert.Equal("{DynamicResource Brush.shell.bg}", (string?)grid.Attribute("Background"));
        Assert.Contains(grid.Elements(Wpf + "Grid"), g => (string?)g.Attribute("Background") == "{DynamicResource Brush.bg.base}");
        Assert.Contains(grid.Elements(Wpf + "Grid"), g => (string?)g.Attribute("Width") == "{DynamicResource Layout.rail-width}");
    }

    [Theory]
    [InlineData("Shell", "ShellWindow.xaml")]
    [InlineData("History", "InboxRailView.xaml")]
    public void NoEmDashes(string folder, string file)
    {
        var root = Load(folder, file);
        Assert.DoesNotContain(root.DescendantNodes().OfType<XComment>(), c => c.Value.Contains('—', StringComparison.Ordinal));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.Contains('—', StringComparison.Ordinal));
    }

    private static XElement Style(string key) =>
        Load("Theme", "Components.xaml").Elements(Wpf + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == key);

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Wpf + "Setter").SingleOrDefault(s => (string?)s.Attribute("Property") == property)?.Attribute("Value");

    private static XElement Load(string folder, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI", folder, file);
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate).Root!;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(file);
    }
}
