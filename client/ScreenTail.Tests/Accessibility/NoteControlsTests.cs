using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// The note as it will land, as the XAML draws it (Spec v0.6.0 §5 S3), read off the files so it holds on
/// the Mac and on every commit: each step's frame is a button into the strip's commands, an inferred step
/// has Looks right with its key, the masks and the count are drawn, the v0.5 screenshot column is gone,
/// and the strip opens over the note. What the commands do is the view models'.
/// </summary>
public sealed class NoteControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Controls = "clr-namespace:ScreenTail.UI.Controls";

    [Fact]
    public void AStepsFrameIsAButtonThatOpensItAndTakesSpace()
    {
        var note = Load("Review", "NoteEditorView.xaml");

        var frame = Assert.Single(Buttons(note), b => (string?)b.Attribute("Tag") == "{Binding Id}");
        Assert.Equal("{Binding DataContext.ShowFrameCommand, RelativeSource={RelativeSource AncestorType=UserControl}}", (string?)frame.Attribute("Command"));
        Assert.Equal("OnFrameKeyDown", (string?)frame.Attribute("PreviewKeyDown"));
        Assert.Equal("{Binding SpokenName}", (string?)frame.Attribute("AutomationProperties.Name"));
        Assert.Equal("{StaticResource StepFrame}", (string?)frame.Attribute("Style"));

        // The picture is the strip's own thumbnail, and every mask is drawn over it in a token colour.
        Assert.Contains(frame.Descendants(Wpf + "Image"), i => (string?)i.Attribute("Source") == "{Binding Item.Thumbnail}");
        var mask = Assert.Single(frame.Descendants(Wpf + "Rectangle"));
        Assert.Equal("{DynamicResource Brush.shell.bg}", (string?)mask.Attribute("Fill"));
        Assert.Equal("{Binding Offset}", (string?)mask.Attribute("Margin"));
    }

    [Fact]
    public void AStepsFrameSaysDelInItsHelpAndTheNoteCarriesTheUndo()
    {
        // Spec v0.6.8 §5 S3: Del on a step's frame deletes it, as in the strip, with the same five-second
        // undo, shown on the note, where the technician is looking.
        var note = Load("Review", "NoteEditorView.xaml");

        var frame = Assert.Single(Buttons(note), b => (string?)b.Attribute("Tag") == "{Binding Id}");
        Assert.Equal("{Binding DataContext.FrameHelp, RelativeSource={RelativeSource AncestorType=UserControl}}", (string?)frame.Attribute("AutomationProperties.HelpText"));
        Assert.Equal("{Binding DataContext.FrameTip, RelativeSource={RelativeSource AncestorType=UserControl}}", (string?)frame.Attribute("ToolTip"));

        // Ctrl+Z is heard on the way down, before a text box takes it for its own undo.
        Assert.Equal("OnNoteKeyDown", (string?)note.Attribute("PreviewKeyDown"));

        var toast = Assert.Single(note.Descendants(Wpf + "Border"), b => (string?)b.Attribute("Style") == "{StaticResource Toast}");
        Assert.Equal("{Binding HasUndoToast, Converter={StaticResource Visible}}", (string?)toast.Attribute("Visibility"));
        var words = Assert.Single(toast.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding UndoToast}");
        Assert.Equal("Assertive", (string?)words.Attribute(Controls + "LiveRegion.Setting"));
        var undo = Assert.Single(toast.Descendants(Wpf + "Button"));
        Assert.Equal("{Binding UndoDeleteCommand}", (string?)undo.Attribute("Command"));
        Assert.Equal("Undo", (string?)undo.Attribute("AutomationProperties.Name"));
        Assert.Equal("Ctrl+Z", (string?)undo.Attribute("AutomationProperties.AcceleratorKey"));
    }

    [Fact]
    public void TheTwoButtonsUnderAnEnlargedScreenshotAreTheSameHeight()
    {
        // "OCR text  T" is a Button.Secondary and "Blur a region  B" a Pill.Toggle. Both had the control
        // height as a minimum only, and the row's bottom margin was on one of them, so the row stretched
        // the other 8 px taller. Both now take the control height itself, and the same margin above and below.
        var strip = Load("Review", "FilmstripView.xaml");

        var ocr = Assert.Single(Buttons(strip), b => (string?)b.Attribute("Content") == "OCR text  T");
        var blur = Assert.Single(strip.Descendants(Wpf + "ToggleButton"), b => (string?)b.Attribute("Content") == "Blur a region  B");

        Assert.Equal("{DynamicResource Layout.control-height}", (string?)ocr.Attribute("Height"));
        Assert.Equal("{DynamicResource Layout.control-height}", (string?)blur.Attribute("Height"));
        Assert.Equal(VerticalMargin(ocr), VerticalMargin(blur));

        // What they do is unchanged.
        Assert.Equal("{Binding ToggleOcrTextCommand}", (string?)ocr.Attribute("Command"));
        Assert.Equal("OnBlurModeChanged", (string?)blur.Attribute("Checked"));
        Assert.Equal("OnBlurModeChanged", (string?)blur.Attribute("Unchecked"));
    }

    private static (string Top, string Bottom) VerticalMargin(XElement element)
    {
        var sides = ((string?)element.Attribute("Margin") ?? "0").Split(',');
        return sides.Length switch
        {
            1 => (sides[0], sides[0]),
            2 => (sides[1], sides[1]),
            _ => (sides[1], sides[3]),
        };
    }

    [Fact]
    public void AnInferredStepSaysSoAndHasLooksRightWithAltCAndEdit()
    {
        var note = Load("Review", "NoteEditorView.xaml");

        var confirm = Assert.Single(Buttons(note), b => (string?)b.Attribute("Command") == "{Binding DataContext.ConfirmStepCommand, RelativeSource={RelativeSource AncestorType=UserControl}}");
        Assert.Equal("{StaticResource Button.Secondary}", (string?)confirm.Attribute("Style"));
        Assert.Equal("Alt+C", (string?)confirm.Attribute("AutomationProperties.AcceleratorKey"));
        Assert.Equal("{Binding ConfirmName}", (string?)confirm.Attribute("AutomationProperties.Name"));
        Assert.Contains(confirm.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "Looks right");

        var edit = Assert.Single(Buttons(note), b => (string?)b.Attribute("Content") == "Edit");
        Assert.Equal("{Binding EditName}", (string?)edit.Attribute("AutomationProperties.Name"));

        // The words beside the glyph, in confidence.low (never colour alone).
        Assert.Contains(note.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding InferredLine}");
        Assert.Contains(note.Descendants(Controls + "Glyph"), g => (string?)g.Attribute("Foreground") == "{DynamicResource Brush.confidence.low}");
    }

    [Fact]
    public void UnderTheNoteAreTheMaskingCountAndTheSaveState()
    {
        var note = Load("Review", "NoteEditorView.xaml");

        Assert.Contains(note.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding MaskingLabel}");
        var save = Assert.Single(note.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding SaveLabel}");
        Assert.Equal("Polite", (string?)save.Attribute(Controls + "LiveRegion.Setting"));
    }

    [Fact]
    public void TheSectionsAreTheTicketsFourInOrderEditedInPlace()
    {
        var note = Load("Review", "NoteEditorView.xaml");
        var headings = note.Descendants(Wpf + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Where(t => t is "Problem" or "Steps" or "Result" or "Follow-ups")
            .ToList();

        Assert.Equal(["Problem", "Steps", "Result", "Follow-ups"], headings);
        Assert.All(note.Descendants(Wpf + "TextBox"), box => Assert.Equal("{StaticResource Input.InPlace}", (string?)box.Attribute("Style")));
    }

    [Fact]
    public void TheScreenshotColumnIsGoneAndTheStripOpensOverTheNote()
    {
        var review = Load("Review", "ReviewPane.xaml");

        Assert.Empty(review.Descendants(Wpf + "GridSplitter"));
        var strip = Assert.Single(review.Descendants(Wpf + "Border"), b => (string?)b.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "StripPane");
        Assert.Equal("{Binding Strip.Shown, Converter={StaticResource Visible}}", (string?)strip.Attribute("Visibility"));
        Assert.Contains(strip.Descendants(Wpf + "Button"), b => (string?)b.Attribute("AutomationProperties.Name") == "Back to the note" && (string?)b.Attribute("AutomationProperties.AcceleratorKey") == "Alt+1");
    }

    [Theory]
    [InlineData("Review", "NoteEditorView.xaml")]
    [InlineData("Review", "ReviewPane.xaml")]
    public void TheNoteUsesTheTokensAndPlainText(string folder, string file)
    {
        var root = Load(folder, file);

        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.StartsWith('#'));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Name.LocalName is "FontSize" or "Foreground" or "Background" or "BorderBrush" or "Fill" && !a.Value.StartsWith('{') && a.Value != "Transparent");
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.Contains('\u2014', StringComparison.Ordinal));
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
