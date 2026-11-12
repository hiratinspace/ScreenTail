using System.Text.Json;
using System.Xml.Linq;

namespace ScreenTail.Tests.Theme;

/// <summary>
/// Spec §7: when Windows high contrast is on, every token maps to the system palette. The dictionary is
/// written by hand while the light and dark ones are generated from shared/design/tokens.json, so a token
/// added to tokens.json arrives in two of the three themes and is a missing resource in the third, at run
/// time, in the theme a technician who needs it has least chance of reporting (2026-10-12 review, T-377).
/// </summary>
public sealed class HighContrastTokensTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HighContrastDefinesEveryTokenTheThemesDo()
    {
        var light = Keys("Tokens.Light.g.xaml");
        var dark = Keys("Tokens.Dark.g.xaml");
        var highContrast = Keys("Tokens.HighContrast.xaml");

        Assert.NotEmpty(light);
        Assert.Equal(light, dark);
        Assert.Empty(light.Except(highContrast));
        Assert.Empty(highContrast.Except(light));
    }

    [Fact]
    public void TheCommonDictionaryIsNotRedefinedByAnyTheme()
    {
        // Sizes and weights live in the common dictionary and are merged under every theme. A theme that
        // redefined one would win or lose by merge order, which nobody would notice until a pill changed size.
        var common = Keys("Tokens.Common.g.xaml");

        Assert.NotEmpty(common);
        foreach (var theme in new[] { "Tokens.Light.g.xaml", "Tokens.Dark.g.xaml", "Tokens.HighContrast.xaml" })
        {
            Assert.Empty(common.Intersect(Keys(theme)));
        }
    }

    [Fact]
    public void EachTokenMapsToTheSystemColourTokensJsonNames()
    {
        // Spec v0.6.0: tokens.json names each colour's system colour (`hc`), so the hand-written dictionary
        // and the token file say the same thing. A token mapped here to a different system colour from the
        // one the file names is a decision made in one place and not the other.
        using var tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Design", "tokens.json")));
        var path = Path.Combine(AppContext.BaseDirectory, "Theme", "Tokens.HighContrast.xaml");
        var colours = XDocument.Load(path).Root!.Elements()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Value.Trim(), StringComparer.Ordinal);

        foreach (var colour in tokens.RootElement.GetProperty("color").EnumerateObject())
        {
            var systemColour = colour.Value.GetProperty("hc").GetString();
            Assert.Equal($"{{x:Static SystemColors.{systemColour}Color}}", colours[$"Token.{colour.Name}"]);
        }
    }

    private static SortedSet<string> Keys(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Theme", file);
        var document = XDocument.Load(path);
        return new SortedSet<string>(
            document.Root!.Elements().Select(e => e.Attribute(X + "Key")?.Value).Where(k => k is not null)!,
            StringComparer.Ordinal);
    }
}
