using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// The setup wizard as the XAML draws it (Spec v0.6.0 §2.0 and §5 S8, as built v0.6.7), read off the files
/// so the frame's tokens, the lockup, the step list's names and the live regions hold on the Mac and on
/// every commit. What each step checks is <c>OnboardingWizard</c>'s; what the list says is
/// <c>SetupSteps</c>'; this holds that the window reaches them.
/// </summary>
public sealed class OnboardingControlsTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Controls = "clr-namespace:ScreenTail.UI.Controls";

    [Fact]
    public void TheStepListIsOnTheFrameUnderTheLockup()
    {
        var frame = Frame();
        Assert.Equal("{DynamicResource Brush.shell.bg}", (string?)frame.Attribute("Background"));
        Assert.Equal("{DynamicResource Layout.rail-width}", (string?)frame.Attribute("Width"));

        // The line down its edge is all that divides the frame from the page in high contrast.
        Assert.Equal("{DynamicResource Brush.shell.line}", (string?)frame.Attribute("BorderBrush"));
        Assert.Equal("0,0,1,0", (string?)frame.Attribute("BorderThickness"));

        var parts = frame.Elements(Wpf + "StackPanel").Single().Elements().ToList();
        Assert.Equal("{StaticResource Brand.Lockup.Frame}", (string?)parts[0].Attribute("Template"));
        Assert.Equal("{Binding Steps}", (string?)parts[1].Attribute("ItemsSource"));
        Assert.Equal(Controls + "Repeater", parts[1].Name);
    }

    [Fact]
    public void TheLockupIsDecorationThatTakesNoFocus()
    {
        var lockup = Assert.Single(Frame().Descendants(Wpf + "Control"));
        Assert.Equal("False", (string?)lockup.Attribute("Focusable"));
        Assert.Equal("False", (string?)lockup.Attribute("IsTabStop"));
    }

    [Fact]
    public void TheLockupIsDrawnInTokensOnTheFilesOwnGrid()
    {
        var template = Load("Theme", "Components.xaml").Elements(Wpf + "ControlTemplate").Single(t => (string?)t.Attribute(Xaml + "Key") == "Brand.Lockup.Frame");

        // The brand file's box, clear space included (Spec §2.0: nothing else enters it), scaled as one
        // piece and never stretched.
        var box = template.Elements(Wpf + "Viewbox").Single();
        Assert.Equal("Uniform", (string?)box.Attribute("Stretch"));
        var grid = box.Elements(Wpf + "Canvas").Single();
        Assert.Equal("330.4", (string?)grid.Attribute("Width"));
        Assert.Equal("96", (string?)grid.Attribute("Height"));

        // Every fill is a token, so the mark follows the theme and high contrast; none is a brand hex.
        var fills = template.Descendants(Wpf + "Path").Select(p => (string?)p.Attribute("Fill")).ToList();
        Assert.NotEmpty(fills);
        Assert.All(fills, f => Assert.StartsWith("{DynamicResource Brush.", f, StringComparison.Ordinal));
        Assert.Contains("{DynamicResource Brush.brand.signature}", fills);
        Assert.Contains("{DynamicResource Brush.shell.text}", fills);
        Assert.Contains("{DynamicResource Brush.shell.bg}", fills);

        // The ribbon is one hook drawn once and turned 180 degrees about the mark's centre (the brand's
        // point symmetry), never redrawn by hand.
        var turn = Assert.Single(template.Descendants(Wpf + "RotateTransform"));
        Assert.Equal("180", (string?)turn.Attribute("Angle"));
        Assert.Equal("32", (string?)turn.Attribute("CenterX"));
        Assert.Equal("32", (string?)turn.Attribute("CenterY"));
        var hooks = template.Descendants(Wpf + "Path").Where(p => (string?)p.Attribute("Fill") == "{DynamicResource Brush.brand.signature}").Select(p => (string?)p.Attribute("Data")).ToList();
        Assert.Equal(2, hooks.Count);
        Assert.Equal(hooks[0], hooks[1]);
    }

    [Fact]
    public void TheLockupsOutlinesAreTheBrandFiles()
    {
        // The hook and its underside's clip are the file's own path data, comma for space.
        var svg = Brand("lockup-horizontal-on-navy.svg");
        var template = Load("Theme", "Components.xaml").Elements(Wpf + "ControlTemplate").Single(t => (string?)t.Attribute(Xaml + "Key") == "Brand.Lockup.Frame");
        var paths = template.Descendants(Wpf + "Path").ToList();

        var hook = (string?)paths.First(p => (string?)p.Attribute("Fill") == "{DynamicResource Brush.brand.signature}").Attribute("Data");
        Assert.Contains("d=\"" + Spaced(hook!) + "\" fill=\"#6A43C4\"", svg, StringComparison.Ordinal);

        var underside = (string?)paths.First(p => p.Attribute("Clip") is not null).Attribute("Clip");
        Assert.Contains("<path d=\"" + Spaced(underside!) + "\"/></clipPath>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepRowIsNamedInWordsAndItsNumberAndTagAreNotReadTwice()
    {
        var row = Resource("Onboarding.StepRow");

        var name = Assert.Single(row.Descendants(Wpf + "TextBlock"));
        Assert.Equal("{Binding Name}", (string?)name.Attribute("Text"));
        Assert.Equal("{Binding Spoken}", (string?)name.Attribute("AutomationProperties.Name"));

        // The tick, the number and "Optional" are Glyphs, which have no automation peer: the spoken name
        // carries all three.
        var silent = row.Descendants(Controls + "Glyph").ToList();
        Assert.Contains(silent, g => (string?)g.Attribute("Text") == "{Binding Number}");
        Assert.Contains(silent, g => (string?)g.Attribute("Text") == "{Binding Tag}");
        Assert.Contains(silent, g => (string?)g.Attribute(Xaml + "Name") == "Tick");
    }

    [Fact]
    public void TheStepInHandDiffersInShapeWeightAndEdgeNotOnlyColour()
    {
        var row = Resource("Onboarding.StepRow");
        var triggers = row.Elements(Wpf + "DataTemplate.Triggers").Single().Elements(Wpf + "DataTrigger").ToList();

        var done = triggers.Single(t => (string?)t.Attribute("Binding") == "{Binding IsDone}").Elements(Wpf + "Setter").ToList();
        Assert.Contains(done, s => Is(s, "Tick", "Visibility", "Visible"));
        Assert.Contains(done, s => Is(s, "Ring", "Visibility", "Collapsed"));
        Assert.Contains(done, s => Is(s, "Number", "Visibility", "Collapsed"));

        var current = triggers.Single(t => (string?)t.Attribute("Binding") == "{Binding IsCurrent}").Elements(Wpf + "Setter").ToList();
        Assert.Contains(current, s => Is(s, "StepName", "FontWeight", "{DynamicResource Weight.semibold}"));
        Assert.Contains(current, s => Is(s, "Ring", "StrokeThickness", "{DynamicResource Focus.width}"));

        // shell.accent is Highlight in high contrast, where shell.hover is the window colour and says nothing.
        Assert.Contains(current, s => Is(s, "Row", "BorderBrush", "{DynamicResource Brush.shell.accent}"));
        Assert.Contains(current, s => Is(s, "Row", "Background", "{DynamicResource Brush.shell.hover}"));
    }

    [Fact]
    public void TheFramesWordsUseTheFramesTokens()
    {
        // Text on shell.bg is shell.text or shell.muted (Spec §2.1), never the panes' text tokens, which
        // are dark on the light theme's navy frame.
        var row = Resource("Onboarding.StepRow");
        foreach (var text in row.Descendants().Where(e => e.Name == Wpf + "TextBlock" || e.Name == Controls + "Glyph"))
        {
            var style = (string?)text.Attribute("Style");
            var colour = (string?)text.Attribute("Foreground");
            Assert.True(
                style is "{StaticResource Text.Frame}" or "{StaticResource Text.FrameMuted}" || colour?.StartsWith("{DynamicResource Brush.shell.", StringComparison.Ordinal) == true,
                $"{text.Name.LocalName} on the frame takes neither a frame text style nor a shell colour");
        }

        Assert.DoesNotContain(Frame().DescendantsAndSelf().Attributes(), a => a.Value.Contains("Brush.text.", StringComparison.Ordinal) || a.Value.Contains("Brush.accent.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTitleTheNoteAndTheBlockerAreAnnounced()
    {
        var window = Load("Onboarding", "OnboardingWindow.xaml");
        foreach (var binding in new[] { "{Binding Title}", "{Binding Blocker}" })
        {
            var text = Assert.Single(window.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == binding);
            Assert.Equal("Polite", (string?)text.Attribute(Controls + "LiveRegion.Setting"));
        }

        // The note is in the foot on every step but Done, where the page says it in full; both are live.
        var notes = window.Descendants(Wpf + "TextBlock").Where(t => (string?)t.Attribute("Text") == "{Binding Note}").ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal("Polite", (string?)n.Attribute(Controls + "LiveRegion.Setting")));
        _ = Assert.Single(notes, n => (string?)n.Attribute("Visibility") == "{Binding NoteVisibility}");

        // "Step 3 of 7" in words, over the title.
        _ = Assert.Single(window.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding Progress}");
    }

    [Fact]
    public void TheFootHasBackSkipAndOneWayOn()
    {
        var window = Load("Onboarding", "OnboardingWindow.xaml");
        var buttons = window.Descendants(Wpf + "Button").ToList();

        var back = Assert.Single(buttons, b => (string?)b.Attribute("Content") == "Back");
        Assert.Equal("{Binding BackCommand}", (string?)back.Attribute("Command"));
        var skip = Assert.Single(buttons, b => (string?)b.Attribute("Content") == "Skip for now");
        Assert.Equal("{Binding SkipVisibility}", (string?)skip.Attribute("Visibility"));

        // Continue and Finish are the same place and never both shown: the foot's one filled button.
        var next = Assert.Single(buttons, b => (string?)b.Attribute("Content") == "Continue");
        var finish = Assert.Single(buttons, b => (string?)b.Attribute("Content") == "Finish");
        Assert.Equal("{StaticResource Button.Primary}", (string?)next.Attribute("Style"));
        Assert.Equal("{StaticResource Button.Primary}", (string?)finish.Attribute("Style"));
        Assert.Equal("{Binding ContinueVisibility}", (string?)next.Attribute("Visibility"));
        Assert.Equal("{Binding FinishVisibility}", (string?)finish.Attribute("Visibility"));
        Assert.Equal("{Binding CanContinue}", (string?)next.Attribute("IsEnabled"));

        // Tab order follows the picture: Back, Skip, then the way on.
        Assert.True(buttons.IndexOf(back) < buttons.IndexOf(skip) && buttons.IndexOf(skip) < buttons.IndexOf(next));
    }

    [Fact]
    public void ThePageIsNoWiderThanTheReadableWidthAndTheWindowFitsTheShellsMinimum()
    {
        var window = Load("Onboarding", "OnboardingWindow.xaml");

        // The page and the foot share one cap, so Continue sits under the page's right edge.
        var capped = window.Descendants(Wpf + "ColumnDefinition").Where(c => (string?)c.Attribute("MaxWidth") == "{DynamicResource Layout.content-max-width}").ToList();
        Assert.Equal(2, capped.Count);

        // The wizard opens no larger than the shell's own minimum (1024 by 680), so it fits any screen the
        // shell does, and its own minimum leaves the page 600 beside the 280 frame.
        Assert.True(double.Parse((string)window.Attribute("Width")!, System.Globalization.CultureInfo.InvariantCulture) <= 1024);
        Assert.True(double.Parse((string)window.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture) <= 680);
        Assert.Equal("880", (string?)window.Attribute("MinWidth"));
        Assert.Equal("560", (string?)window.Attribute("MinHeight"));
    }

    [Theory]
    [InlineData("Onboarding", "OnboardingWindow.xaml")]
    public void NoEmDashes(string folder, string file)
    {
        var root = Load(folder, file);
        Assert.DoesNotContain(root.DescendantNodes().OfType<XComment>(), c => c.Value.Contains('—', StringComparison.Ordinal));
        Assert.DoesNotContain(root.Descendants().Attributes(), a => a.Value.Contains('—', StringComparison.Ordinal));
    }

    private static bool Is(XElement setter, string target, string property, string value) =>
        (string?)setter.Attribute("TargetName") == target && (string?)setter.Attribute("Property") == property && (string?)setter.Attribute("Value") == value;

    /// <summary>XAML's "M60,4 L54,16" as the SVG writes it, "M60 4 L54 16".</summary>
    private static string Spaced(string data) => data.Replace(',', ' ');

    private static XElement Frame() =>
        Load("Onboarding", "OnboardingWindow.xaml").Elements(Wpf + "Grid").Single().Elements(Wpf + "Border").Single();

    private static XElement Resource(string key) =>
        Load("Onboarding", "OnboardingWindow.xaml").Elements(Wpf + "Window.Resources").Single().Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key);

    private static string Brand(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "shared", "design", "brand", file);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(file);
    }

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
