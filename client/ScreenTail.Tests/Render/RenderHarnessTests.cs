using System.Text.RegularExpressions;
using System.Xml.Linq;
using ScreenTail.UI.Render;

namespace ScreenTail.Tests.Render;

/// <summary>
/// The render harness's logic that has no WPF in it (ST-016): the fixed contrast palettes, and the index
/// the harness writes beside its renders. The renders themselves run on the hosted Windows runner.
/// </summary>
public sealed partial class RenderHarnessTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] InThemeOrder = ["shell-review-dark.png", "shell-review-light.png", "shell-review-hcblack.png", "shell-review-hcwhite.png"];

    public static TheoryData<string> Palettes => ["Night sky", "Desert"];

    [Fact]
    public void ThePaletteTableMapsEveryTokenTheWayTheHighContrastDictionaryDoes()
    {
        // The dictionary is the product's mapping; the table is the harness's copy of it. A token remapped
        // in one and not the other would render the black and white contrast themes wrong, quietly.
        var path = Path.Combine(AppContext.BaseDirectory, "Theme", "Tokens.HighContrast.xaml");
        var fromXaml = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in XDocument.Load(path).Root!.Elements().Where(e => e.Name.LocalName == "Color"))
        {
            var key = element.Attribute(X + "Key")!.Value;
            var match = StaticColour().Match(element.Value);
            Assert.True(match.Success, $"{key} is not an x:Static SystemColors value: '{element.Value}'.");
            fromXaml[key["Token.".Length..]] = match.Groups[1].Value;
        }

        Assert.NotEmpty(fromXaml);
        Assert.Equal(fromXaml, new SortedDictionary<string, string>(ContrastPalette.TokenSystemColours.ToDictionary(), StringComparer.Ordinal));
    }

    [Fact]
    public void EveryBrushInTheDictionaryUsesTheSameSystemColourAsItsToken()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Theme", "Tokens.HighContrast.xaml");
        foreach (var element in XDocument.Load(path).Root!.Elements().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            var key = element.Attribute(X + "Key")!.Value["Brush.".Length..];
            var match = DynamicColour().Match(element.Attribute("Color")!.Value);
            Assert.True(match.Success, $"Brush.{key} does not bind a SystemColors key.");
            Assert.Equal(ContrastPalette.TokenSystemColours[key], match.Groups[1].Value);
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EachPaletteDefinesEverySystemColourTheTokensUse(string name)
    {
        var palette = ByName(name);
        foreach (var system in ContrastPalette.TokenSystemColours.Values.Distinct())
        {
            Assert.True(palette.SystemColours.ContainsKey(system), $"{name} has no {system}.");
        }

        Assert.Equal(ContrastPalette.TokenSystemColours.Count, palette.Tokens().Count);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EachPaletteIsReallyHighContrast(string name)
    {
        // A palette that was not would make the renders lie the other way: a contrast problem seen in a
        // render that the real theme does not have.
        var colours = ByName(name).SystemColours;
        Assert.True(Ratio(colours["WindowText"], colours["Window"]) >= 7, $"{name}: text on window.");
        Assert.True(Ratio(colours["HighlightText"], colours["Highlight"]) >= 4.5, $"{name}: selected text.");
        Assert.True(Ratio(colours["GrayText"], colours["Window"]) >= 4.5, $"{name}: inactive text.");
        Assert.True(Ratio(colours["HotTrack"], colours["Window"]) >= 4.5, $"{name}: links.");
    }

    [Fact]
    public void BlackIsDarkAndWhiteIsLight()
    {
        Assert.Equal((0, 0, 0), ContrastPalette.Rgb(ContrastPalette.NightSky.SystemColours["Window"]));
        Assert.True(Luminance(ContrastPalette.Desert.SystemColours["Window"]) > 0.9);
    }

    [Fact]
    public void TheThemesAreNamedOnceEachAndOnlyTheContrastOnesHaveAPalette()
    {
        Assert.Equal(["dark", "light", "hcblack", "hcwhite"], RenderThemes.All.Select(t => t.Suffix()));
        Assert.Null(RenderTheme.Dark.Palette());
        Assert.Null(RenderTheme.Light.Palette());
        Assert.Same(ContrastPalette.NightSky, RenderTheme.HcBlack.Palette());
        Assert.Same(ContrastPalette.Desert, RenderTheme.HcWhite.Palette());
        Assert.Equal("settings-privacy-hcwhite.png", RenderThemes.FileName("settings-privacy", RenderTheme.HcWhite));
    }

    [Theory]
    [InlineData("shell-review-no-psa-hcblack.png", "shell", "shell-review-no-psa", "hcblack")]
    [InlineData("settings-privacy-dark.png", "settings", "settings-privacy", "dark")]
    [InlineData("shell-review-150pct-light.png", "shell", "shell-review-150pct", "light")]
    [InlineData("gallery-dark.png", "gallery", "gallery", "dark")]
    [InlineData("hud-expanded-hcwhite.png", "hud", "hud-expanded", "hcwhite")]
    [InlineData("oddity.png", "oddity", "oddity", null)]
    [InlineData("shell-review-highcontrast.png", "shell", "shell-review-highcontrast", null)]
    public void ANameSplitsIntoAreaScreenAndTheme(string file, string area, string screen, string? theme)
    {
        Assert.Equal((area, screen, theme), RenderIndex.Parse(file));
    }

    [Fact]
    public void TheIndexListsEveryRenderGroupedByScreenWithTheThemesInOrder()
    {
        var page = RenderIndex.Build(
        [
            "shell-review-hcwhite.png",
            "shell-review-dark.png",
            "shell-review-light.png",
            "shell-review-hcblack.png",
            "hud-recording-dark.png",
            "notes.txt",
        ]);

        Assert.Contains("5 images", page, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", page, StringComparison.Ordinal);
        Assert.True(page.IndexOf("<h2 id=\"hud\">", StringComparison.Ordinal) < page.IndexOf("<h2 id=\"shell\">", StringComparison.Ordinal));

        var order = InThemeOrder
            .Select(name => page.IndexOf($"src=\"{name}\"", StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void TheIndexEscapesWhatItIsGiven()
    {
        var page = RenderIndex.Build(["a<b>&c-dark.png"]);

        Assert.DoesNotContain("<b>", page, StringComparison.Ordinal);
        Assert.Contains("a&lt;b&gt;&amp;c", page, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteIndexesTheFolder()
    {
        var directory = Directory.CreateTempSubdirectory("st-render-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "settings-privacy-dark.png"), [1]);
            File.WriteAllBytes(Path.Combine(directory, "settings-privacy-hcblack.png"), [1]);
            RenderIndex.Write(directory);

            var page = File.ReadAllText(Path.Combine(directory, RenderIndex.FileName));
            Assert.Contains("settings-privacy-dark.png", page, StringComparison.Ordinal);
            Assert.Contains("settings-privacy-hcblack.png", page, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ContrastPalette ByName(string name) => name switch
    {
        "Night sky" => ContrastPalette.NightSky,
        "Desert" => ContrastPalette.Desert,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = ContrastPalette.Rgb(hex);
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    [GeneratedRegex(@"^\{x:Static SystemColors\.(\w+)Color\}$")]
    private static partial Regex StaticColour();

    [GeneratedRegex(@"^\{DynamicResource \{x:Static SystemColors\.(\w+)ColorKey\}\}$")]
    private static partial Regex DynamicColour();
}
