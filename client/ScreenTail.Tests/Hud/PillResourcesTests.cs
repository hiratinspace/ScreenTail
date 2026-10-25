using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScreenTail.Tests.Hud;

/// <summary>
/// Every style and brush the pill asks for exists, in every theme (INV-4, ST-072).
///
/// The pill looks its styles up dynamically so it can be built on a bare UI thread in the shell's quit
/// and re-dock tests. A static lookup of a key that is not there throws as the window is built; a dynamic
/// one quietly falls back to WPF's default, which on the pill is black text on a dark surface: a pill on
/// a monitor, reporting itself as showing, whose words nobody can read. The render harness checks the
/// glyph's colour and the surface, not the words, so this holds the keys instead.
/// </summary>
public sealed partial class PillResourcesTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Tokens.Dark.g.xaml")]
    [InlineData("Tokens.Light.g.xaml")]
    [InlineData("Tokens.HighContrast.xaml")]
    public void EveryResourceThePillAsksForIsDefined(string theme)
    {
        var pill = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Hud", "HudWindow.xaml"));
        var asked = Lookup().Matches(pill).Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);

        var defined = new HashSet<string>(StringComparer.Ordinal);
        defined.UnionWith(Keys(XDocument.Parse(pill)));
        foreach (var file in new[] { "Tokens.Common.g.xaml", "Components.xaml", theme })
        {
            defined.UnionWith(Keys(XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Theme", file))));
        }

        // The control: the pill asks for its styles and its surface, so an empty set cannot pass.
        Assert.Contains("Text.Body", asked);
        Assert.Contains("Brush.bg.surface", asked);
        Assert.Empty(asked.Except(defined));
    }

    private static IEnumerable<string> Keys(XDocument document) =>
        document.Descendants().Select(e => e.Attribute(X + "Key")?.Value).OfType<string>();

    [GeneratedRegex(@"\{(?:Dynamic|Static)Resource\s+(?<key>[^}\s]+)\s*\}")]
    private static partial Regex Lookup();
}
