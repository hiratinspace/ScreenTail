using System.Xml.Linq;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// The publish receipt's brand mark (Spec v0.6.0 §2.0 and §5 S3, built v0.6.12), read off the XAML: the mid
/// glyph from the brand file, drawn in tokens on the receipt's frame plate, at a size the ladder allows,
/// and decoration that takes no focus.
/// </summary>
public sealed class ReceiptMarkTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheReceiptPlateCarriesTheMidGlyphAtALadderSize()
    {
        var mark = Load("Review", "Publish", "PublishPane.xaml").Descendants(Wpf + "Control").Single(c => (string?)c.Attribute(Xaml + "Name") == "ReceiptMark");

        Assert.Equal("{StaticResource Brand.Glyph.Frame}", (string?)mark.Attribute("Template"));
        Assert.Equal("False", (string?)mark.Attribute("Focusable"));
        Assert.Equal("False", (string?)mark.Attribute("IsTabStop"));

        // §2.0's size ladder: the mid glyph is for 28 to 48 px.
        var size = double.Parse((string)mark.Attribute("Width")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(size, double.Parse((string)mark.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(size, 28, 48);

        // On the frame plate, the ground §2.0 allows (never mid violet).
        var plate = mark.Ancestors(Wpf + "Border").First();
        Assert.Equal("{DynamicResource Brush.shell.bg}", (string?)plate.Attribute("Background"));
    }

    [Fact]
    public void TheGlyphIsTheBrandFileDrawnInTokens()
    {
        var template = Load("Theme", "Components.xaml").Elements(Wpf + "ControlTemplate").Single(t => (string?)t.Attribute(Xaml + "Key") == "Brand.Glyph.Frame");
        var svg = Brand("glyph-mid-on-navy.svg");

        var grid = template.Elements(Wpf + "Viewbox").Single().Elements(Wpf + "Canvas").Single();
        Assert.Equal("80", (string?)grid.Attribute("Width"));
        Assert.Equal("80", (string?)grid.Attribute("Height"));

        var paths = template.Descendants(Wpf + "Path").ToList();
        Assert.All(paths, p => Assert.StartsWith("{DynamicResource Brush.", (string?)p.Attribute("Fill"), StringComparison.Ordinal));

        // The ribbon is the file's hook, twice, the second turned 180 degrees about the mark's centre.
        var hooks = paths.Where(p => (string?)p.Attribute("Fill") == "{DynamicResource Brush.brand.signature}").Select(p => (string)p.Attribute("Data")!).ToList();
        Assert.Equal(2, hooks.Count);
        Assert.All(hooks, h => Assert.Contains("d=\"" + h.Replace(',', ' ') + "\" fill=\"#6A43C4\"", svg, StringComparison.Ordinal));
        var turn = Assert.Single(template.Descendants(Wpf + "RotateTransform"));
        Assert.Equal("180", (string?)turn.Attribute("Angle"));

        // The two note lines of the mid glyph, where the file puts them.
        var rects = template.Descendants(Wpf + "RectangleGeometry").Select(r => (string?)r.Attribute("Rect")).ToList();
        Assert.Contains("20,26,22,4", rects);
        Assert.Contains("20,34,14,4", rects);
        Assert.Contains("<rect x=\"20\" y=\"26\" width=\"22\" height=\"4\"", svg, StringComparison.Ordinal);
        Assert.Contains("<rect x=\"20\" y=\"34\" width=\"14\" height=\"4\"", svg, StringComparison.Ordinal);
    }

    private static string Brand(string file) => File.ReadAllText(Find("shared", "design", "brand", file));

    private static XElement Load(params string[] parts) => XDocument.Load(Find(["client", "ScreenTail.UI", .. parts])).Root!;

    private static string Find(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
