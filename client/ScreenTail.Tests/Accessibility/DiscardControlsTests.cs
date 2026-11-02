using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// Review's Discard and History's bulk discard as the XAML draws them (Spec §5 S3, S4; §7), read off the
/// files so it holds on the Mac and on every commit. What the commands do is the view models' and is
/// tested on Windows; this holds that the buttons exist, are bound to those commands, are reachable and
/// named, and are styled from the tokens rather than from literals.
/// </summary>
public sealed class DiscardControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>ScreenTail.UI.Controls, where LiveRegion.Setting (a live region that is announced, ST-084) lives.</summary>
    private static readonly XNamespace Controls = "clr-namespace:ScreenTail.UI.Controls";

    [Fact]
    public void ReviewsHeaderHasAGhostDiscardBoundToTheNotesCommand()
    {
        var review = Load("Review", "ReviewPane.xaml");

        var discard = Assert.Single(Buttons(review), b => (string?)b.Attribute("Command") == "{Binding Note.DiscardCommand}");
        Assert.Equal("{StaticResource Button.GhostDanger}", (string?)discard.Attribute("Style"));
        Assert.False(string.IsNullOrWhiteSpace((string?)discard.Attribute("AutomationProperties.Name")));
        Assert.Equal("Discard", (string?)discard.Attribute("Content"));
        Assert.NotEqual("False", (string?)discard.Attribute("IsTabStop"));

        // Why it did not happen is said beside it, in a region Narrator reads out when it changes.
        var reason = Assert.Single(review.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding Note.DiscardError}");
        Assert.Equal("Assertive", (string?)reason.Attribute(Controls + "LiveRegion.Setting"));
    }

    [Fact]
    public void TheGhostDangerButtonIsTheGhostButtonTurningDangerOnHover()
    {
        // Spec §5 S3: "ghost, danger on hover". Built on the ghost button, with the hover taking the danger
        // tint and the danger colour from the tokens, and no colour written into the style.
        var components = Load("Theme", "Components.xaml");

        var style = Assert.Single(components.Descendants(Wpf + "Style"), s => (string?)s.Attribute(X + "Key") == "Button.GhostDanger");
        Assert.Equal("{StaticResource Button.Ghost}", (string?)style.Attribute("BasedOn"));
        var hover = Assert.Single(style.Descendants(Wpf + "Trigger"), t => (string?)t.Attribute("Property") == "IsMouseOver");
        var setters = hover.Elements(Wpf + "Setter").ToDictionary(s => (string)s.Attribute("Property")!, s => (string)s.Attribute("Value")!);
        Assert.Equal("{DynamicResource Brush.bg.danger}", setters["Background"]);
        Assert.Equal("{DynamicResource Brush.state.danger}", setters["Foreground"]);
        Assert.DoesNotContain(style.Descendants().Attributes(), a => a.Value.StartsWith('#'));
    }

    [Fact]
    public void HistoryAllowsAMultipleSelectionAndOffersBulkDiscard()
    {
        var history = Load("History", "HistoryPane.xaml");

        var table = Assert.Single(history.Descendants(Wpf + "DataGrid"));
        Assert.Equal("Extended", (string?)table.Attribute("SelectionMode"));
        Assert.Equal("OnSelectionChanged", (string?)table.Attribute("SelectionChanged"));

        var bulk = Assert.Single(Buttons(history), b => (string?)b.Attribute("Command") == "{Binding BulkDiscardCommand}");
        Assert.Equal("Bulk discard", (string?)bulk.Attribute("Content"));
        Assert.False(string.IsNullOrWhiteSpace((string?)bulk.Attribute("AutomationProperties.Name")));
        Assert.Equal("{StaticResource Button.Danger}", (string?)bulk.Attribute("Style"));

        var reason = Assert.Single(history.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding DiscardError}");
        Assert.Equal("Assertive", (string?)reason.Attribute(Controls + "LiveRegion.Setting"));
    }

    [Theory]
    [InlineData("Review", "ReviewPane.xaml")]
    [InlineData("History", "HistoryPane.xaml")]
    [InlineData("Review", "ConfirmTypedDialog.xaml")]
    public void TheNewControlsUseTheTokensAndPlainText(string folder, string file)
    {
        // No colour literal on any element, and no font size or control height outside the styles. Copy
        // uses a colon where a dash was, as the v0.5 copy rule asks.
        var root = Load(folder, file);

        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.StartsWith('#'));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Name.LocalName is "FontSize" or "Foreground" or "Background" && !a.Value.StartsWith('{'));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.Contains('\u2014', StringComparison.Ordinal));
    }

    [Fact]
    public void TheTypedDialogPutsTheCaretInTheBoxAndNamesItsButtons()
    {
        // The technician opens it to type, so the box has focus when it appears; Esc keeps the session.
        var dialog = Load("Review", "ConfirmTypedDialog.xaml");

        Assert.Equal("{Binding ElementName=Typed}", (string?)dialog.Attribute("FocusManager.FocusedElement"));
        var buttons = Buttons(dialog).ToList();
        Assert.Contains(buttons, b => (string?)b.Attribute("IsCancel") == "True");
        Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("AutomationProperties.Name"))));
    }

    private static IEnumerable<XElement> Buttons(XElement root) => root.Descendants(Wpf + "Button");

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
