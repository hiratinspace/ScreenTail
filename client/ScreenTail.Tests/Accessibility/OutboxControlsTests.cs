using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// The Outbox envelope and receipt as the XAML draws them (Spec v0.6.0 §5 S3), read off the files so the
/// keyboard paths and the gate hold on the Mac and on every commit. What each command does is the view
/// model's; this holds that the keys reach it, that Publish is the screen's one filled button with its
/// reason beside it, and that every state on the receipt is a word, a glyph and a colour.
/// </summary>
public sealed class OutboxControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Controls = "clr-namespace:ScreenTail.UI.Controls";

    [Fact]
    public void CtrlEnterPublishesAndCtrlTChangesTheTicket()
    {
        var review = Load("Review", "ReviewPane.xaml");
        var keys = review.Descendants(Wpf + "KeyBinding").ToList();

        Assert.Contains(keys, k => (string?)k.Attribute("Modifiers") == "Ctrl" && (string?)k.Attribute("Key") == "Return" && (string?)k.Attribute("Command") == "{Binding Publish.PublishCommand}");
        Assert.Contains(keys, k => (string?)k.Attribute("Modifiers") == "Ctrl" && (string?)k.Attribute("Key") == "T" && (string?)k.Attribute("Command") == "{Binding Publish.ChangeTicketCommand}");
    }

    [Fact]
    public void EnterOnTheReceiptOpensTheNextSession()
    {
        var receipt = Load("Review/Publish", "PublishPane.xaml");

        var enter = Assert.Single(receipt.Descendants(Wpf + "KeyBinding"), k => (string?)k.Attribute("Key") == "Return");
        Assert.Null((string?)enter.Attribute("Modifiers"));
        Assert.Equal("{Binding OpenNextCommand}", (string?)enter.Attribute("Command"));

        var next = Assert.Single(Buttons(receipt), b => (string?)b.Attribute("Command") == "{Binding OpenNextCommand}");
        Assert.Equal("{StaticResource Button.Primary}", (string?)next.Attribute("Style"));
        Assert.Contains("Open next", (string?)next.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
    }

    [Fact]
    public void PublishIsTheEnvelopesOneFilledButtonWithItsHintAndReasonBesideIt()
    {
        var review = Load("Review", "ReviewPane.xaml");
        var envelope = Load("Review/Publish", "EnvelopeView.xaml");

        var publish = Assert.Single(Buttons(review), b => (string?)b.Attribute("Command") == "{Binding Publish.PublishCommand}");
        Assert.Equal("{StaticResource Button.Primary}", (string?)publish.Attribute("Style"));
        Assert.Equal("{Binding Publish.CanPublish}", (string?)publish.Attribute("IsEnabled"));
        Assert.Equal("{Binding Publish.BlockedBecause}", (string?)publish.Attribute("AutomationProperties.HelpText"));
        Assert.Contains(publish.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "Ctrl+Enter");

        // The only filled button in the envelope: Discard stays ghost, Change is a link.
        Assert.Single(Buttons(review).Concat(Buttons(envelope)), b => (string?)b.Attribute("Style") == "{StaticResource Button.Primary}");

        // The reason beside it, read out as it changes (v0.4.3), never only in a tooltip.
        var reason = Assert.Single(review.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding Publish.BlockedBecause}");
        Assert.Equal("Polite", (string?)reason.Attribute(Controls + "LiveRegion.Setting"));
    }

    [Fact]
    public void TheEnvelopeHasItsFourLinesAndChangeCarriesItsKey()
    {
        var envelope = Load("Review/Publish", "EnvelopeView.xaml");
        var labels = envelope.Descendants(Wpf + "TextBlock").Select(t => (string?)t.Attribute("Text")).ToList();

        Assert.Contains("To", labels);
        Assert.Contains("As", labels);
        Assert.Contains("Time", labels);
        Assert.Contains("Also", labels);
        Assert.Contains("Ctrl+T", labels);

        var change = Assert.Single(Buttons(envelope), b => (string?)b.Attribute("Command") == "{Binding ChangeTicketCommand}");
        Assert.Equal("{Binding ChangeLabel}", (string?)change.Attribute("Content"));

        // As is a visible two-way choice, Internal first (v0.4.1 Q2).
        var radios = envelope.Descendants(Wpf + "RadioButton").ToList();
        Assert.Equal(2, radios.Count);
        Assert.Equal("{Binding IsInternal}", (string?)radios[0].Attribute("IsChecked"));
        Assert.Equal("{Binding IsDiscussion}", (string?)radios[1].Attribute("IsChecked"));
        Assert.All(radios, r => Assert.Equal("{StaticResource Segment}", (string?)r.Attribute("Style")));
    }

    [Fact]
    public void TheReceiptHeadingIsAWordAGlyphAndAColour()
    {
        var receipt = Load("Review/Publish", "PublishPane.xaml");

        var heading = Assert.Single(receipt.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding ReceiptHeading}");
        Assert.Equal("Assertive", (string?)heading.Attribute(Controls + "LiveRegion.Setting"));

        var glyph = Assert.Single(receipt.Descendants(Controls + "Glyph"), g => (string?)g.Attribute("Text") == "{Binding ReceiptGlyph}");
        var colours = glyph.Descendants(Wpf + "Setter").Select(s => (string?)s.Attribute("Value")).ToHashSet();
        Assert.Contains("{DynamicResource Brush.shell.published}", colours);
        Assert.Contains("{DynamicResource Brush.shell.paused}", colours);
        Assert.Contains("{DynamicResource Brush.shell.recording}", colours);

        // The receipt rule is the ribbon band (Spec §2.1).
        Assert.Contains(receipt.Descendants(Wpf + "Border"), b => (string?)b.Attribute("BorderBrush") == "{DynamicResource Brush.brand.ribbon.band}");
    }

    [Theory]
    [InlineData("Review/Publish", "EnvelopeView.xaml")]
    [InlineData("Review/Publish", "PublishPane.xaml")]
    public void TheOutboxUsesTheTokensAndPlainText(string folder, string file)
    {
        var root = Load(folder, file);

        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.StartsWith('#'));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Name.LocalName is "FontSize" or "Foreground" or "Background" or "BorderBrush" && !a.Value.StartsWith('{') && a.Value != "Transparent");
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.Contains('—', StringComparison.Ordinal));
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
