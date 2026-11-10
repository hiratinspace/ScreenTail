using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// History's "Not confirmed" badge and its Mark as published button as the XAML draws them (ST-093,
/// contract v10; Spec §5 S4, §7), read off the files so it holds on the Mac and on every commit. What
/// the command does is the view model's and is tested on Windows; this holds that the badge is drawn
/// beside the status from the tokens, is named, and is not a tab stop, and that the button exists, is
/// bound, is named and is a standard button (so it has the standard hit target).
/// </summary>
public sealed class NotConfirmedControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheBadgeSitsBesideTheStatusBadgeAndIsNamed()
    {
        var history = Load("History", "HistoryPane.xaml");

        var column = Assert.Single(history.Descendants(Wpf + "DataGridTemplateColumn"), c => (string?)c.Attribute("Header") == "Status");
        var badges = column.Descendants(Wpf + "ContentControl").ToList();
        Assert.Equal(2, badges.Count);

        // The status first, as it always was, so sorting and the words do not change.
        Assert.Equal("{StaticResource Badge}", (string?)badges[0].Attribute("Style"));
        Assert.Equal("{Binding Status}", (string?)badges[0].Attribute("Content"));
        Assert.Equal("Status", (string?)column.Attribute("SortMemberPath"));

        var notConfirmed = badges[1];
        Assert.Equal("{StaticResource Badge.Unconfirmed}", (string?)notConfirmed.Attribute("Style"));
        Assert.Equal("{Binding NotConfirmedLabel}", (string?)notConfirmed.Attribute("Content"));
        Assert.Equal("{Binding NotConfirmedVisibility}", (string?)notConfirmed.Attribute("Visibility"));
        Assert.False(string.IsNullOrWhiteSpace((string?)notConfirmed.Attribute("AutomationProperties.Name")));
        Assert.False(string.IsNullOrWhiteSpace((string?)notConfirmed.Attribute("ToolTip")));

        // The row is what a screen reader lands on, and its name says it.
        var row = Assert.Single(history.Descendants(Wpf + "Style"), s => (string?)s.Attribute("TargetType") == "DataGridRow");
        Assert.Contains(row.Elements(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "AutomationProperties.Name" && (string?)s.Attribute("Value") == "{Binding Spoken}");
    }

    [Fact]
    public void TheBadgeIsTheBadgeInTheWarningTokensDistinctFromPublished()
    {
        var components = Load("Theme", "Components.xaml");
        var unconfirmed = Setters(components, "Badge.Unconfirmed");
        var published = Setters(components, "Badge.Published");

        Assert.Equal("{StaticResource Badge}", (string?)Style(components, "Badge.Unconfirmed").Attribute("BasedOn"));
        Assert.Equal("{DynamicResource Brush.bg.warning}", unconfirmed["Background"]);
        Assert.Equal("{DynamicResource Brush.state.paused}", unconfirmed["BorderBrush"]);
        Assert.Equal("{DynamicResource Brush.text.primary}", unconfirmed["Foreground"]);
        Assert.NotEqual(published["Background"], unconfirmed["Background"]);
        Assert.NotEqual(published["BorderBrush"], unconfirmed["BorderBrush"]);

        // Not the danger tokens a failure is drawn in, and no colour written into the style.
        Assert.DoesNotContain(unconfirmed.Values, v => v.Contains("danger", StringComparison.Ordinal));
        Assert.DoesNotContain(Style(components, "Badge.Unconfirmed").Descendants().Attributes(), a => a.Value.StartsWith('#'));

        // The base badge is not focusable, so the new one adds no tab stop to a row.
        Assert.Equal("False", Setters(components, "Badge")["Focusable"]);
    }

    [Fact]
    public void MarkAsPublishedIsAStandardNamedButtonBoundToItsCommand()
    {
        var history = Load("History", "HistoryPane.xaml");

        var mark = Assert.Single(history.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Command") == "{Binding MarkPublishedCommand}");
        Assert.Equal("Mark as published", (string?)mark.Attribute("Content"));
        Assert.Equal(Core.Shell.MarkAsPublished.Action, (string?)mark.Attribute("Content"));
        Assert.Equal("{StaticResource Button.Secondary}", (string?)mark.Attribute("Style"));
        Assert.False(string.IsNullOrWhiteSpace((string?)mark.Attribute("AutomationProperties.Name")));
        Assert.False(string.IsNullOrWhiteSpace((string?)mark.Attribute("AutomationProperties.HelpText")));
        Assert.NotEqual("False", (string?)mark.Attribute("IsTabStop"));

        // No size of its own: the height, and so the hit target, is the button style's.
        Assert.Null(mark.Attribute("Height"));
        Assert.Null(mark.Attribute("MinHeight"));

        // Not the danger button, and beside it rather than in its place.
        var bulk = Assert.Single(history.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Command") == "{Binding BulkDiscardCommand}");
        Assert.Same(mark.Parent, bulk.Parent);
        Assert.True(mark.IsBefore(bulk));
    }

    [Fact]
    public void TheNewCopyIsPlain()
    {
        var history = Load("History", "HistoryPane.xaml");

        Assert.DoesNotContain(history.DescendantsAndSelf().Attributes(), a => a.Value.Contains('—', StringComparison.Ordinal));
        Assert.DoesNotContain(history.Descendants().Attributes(), a => a.Value.StartsWith('#'));
    }

    private static XElement Style(XElement root, string key) =>
        Assert.Single(root.Descendants(Wpf + "Style"), s => (string?)s.Attribute(X + "Key") == key);

    private static Dictionary<string, string> Setters(XElement root, string key) =>
        Style(root, key).Elements(Wpf + "Setter")
            .Where(s => s.Attribute("Value") is not null)
            .ToDictionary(s => (string)s.Attribute("Property")!, s => (string)s.Attribute("Value")!);

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
