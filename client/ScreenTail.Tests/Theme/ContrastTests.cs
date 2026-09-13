using System.Globalization;
using System.Text.Json;

namespace ScreenTail.Tests.Theme;

/// <summary>
/// Spec §2.1: every text/background pair is at least 4.5:1 in both themes (WCAG 2.1 AA). Reads
/// shared/design/tokens.json directly, so a token change that breaks contrast fails the build.
/// </summary>
public class ContrastTests
{
    private static readonly string TokensPath = Path.Combine(AppContext.BaseDirectory, "Design", "tokens.json");
    private static readonly JsonDocument Tokens = JsonDocument.Parse(File.ReadAllText(TokensPath));

    public static TheoryData<string, string, string> TextOnBackgroundPairs
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            var contrast = Tokens.RootElement.GetProperty("contrast");
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var text in contrast.GetProperty("text").EnumerateArray())
                {
                    foreach (var background in contrast.GetProperty("backgrounds").EnumerateArray())
                    {
                        data.Add(theme, text.GetString()!, background.GetString()!);
                    }
                }

                foreach (var pair in contrast.GetProperty("pairs").EnumerateArray())
                {
                    data.Add(theme, pair[0].GetString()!, pair[1].GetString()!);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TextOnBackgroundPairs))]
    public void TextIsReadableOnItsBackground(string theme, string text, string background)
    {
        var minimum = Tokens.RootElement.GetProperty("contrast").GetProperty("minimum").GetDouble();

        var ratio = ContrastRatio(Hex(theme, text), Hex(theme, background));

        Assert.True(ratio >= minimum, $"{theme}: {text} on {background} is {ratio:0.00}:1, below {minimum}:1");
    }

    public static TheoryData<string, string, string> IndicatorPairs
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            var contrast = Tokens.RootElement.GetProperty("contrast");
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var indicator in contrast.GetProperty("indicators").EnumerateArray())
                {
                    foreach (var background in contrast.GetProperty("backgrounds").EnumerateArray())
                    {
                        data.Add(theme, indicator.GetString()!, background.GetString()!);
                    }
                }

            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(IndicatorPairs))]
    public void AnIndicatorIsDistinguishableFromItsBackground(string theme, string indicator, string background)
    {
        // Spec v0.4.2 confines state colours to a rule, a border, a dot or a glyph, so WCAG's non-text
        // 3:1 is the bar rather than AA. They were checked against nothing at all before: seven colour
        // tokens sat outside every list, and the Review hi-fi was quietly using one of them for 11px
        // text at 3.30:1 — under AA, three paragraphs below a comment saying not to.
        var minimum = Tokens.RootElement.GetProperty("contrast").GetProperty("indicator_minimum").GetDouble();

        var ratio = ContrastRatio(Hex(theme, indicator), Hex(theme, background));

        Assert.True(ratio >= minimum, $"{theme}: {indicator} on {background} is {ratio:0.00}:1, below {minimum}:1");
    }

    public static TheoryData<string, string, string> StructuralPairs
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            var contrast = Tokens.RootElement.GetProperty("contrast");
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var structural in contrast.GetProperty("structural").EnumerateArray())
                {
                    foreach (var background in contrast.GetProperty("backgrounds").EnumerateArray())
                    {
                        data.Add(theme, structural.GetString()!, background.GetString()!);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StructuralPairs))]
    public void ADividerIsNotTheSameColourAsWhatItDivides(string theme, string border, string background)
    {
        // Deliberately not 3:1. WCAG 1.4.11 covers visuals that are the *sole* means of identifying a
        // component or its state, and a divider is not one: a control is identified by its own surface
        // and its label, and the focus ring is accent.primary, which sits in the text list at 4.5:1.
        // Holding dividers to 3:1 would force a palette the spec never asked for; holding them to
        // nothing would let one become invisible. This asserts the weaker true thing rather than
        // inventing a number that happens to fit today's colours.
        var ratio = ContrastRatio(Hex(theme, border), Hex(theme, background));

        Assert.True(ratio > 1.0, $"{theme}: {border} is the same colour as {background}, so it draws nothing");
    }

    [Fact]
    public void EveryColourHasToSayWhatItIsAllowedToDo()
    {
        // The structural fix. The lists were hand-kept, so a new token was unchecked until somebody
        // remembered to add it — and nothing said they had forgotten. Now a token that is neither text,
        // nor a background, nor an indicator, nor structural, nor half of a pair fails here, and whoever
        // adds it has to decide which it is.
        var contrast = Tokens.RootElement.GetProperty("contrast");
        var classified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in new[] { "text", "backgrounds", "indicators", "structural" })
        {
            foreach (var name in contrast.GetProperty(list).EnumerateArray())
            {
                classified.Add(name.GetString()!);
            }
        }

        // Both halves. A colour that only ever meets one specific other is declared and checked by the
        // pair itself — accent.primary.hover is never a surface for anything but text.on-accent, so
        // listing it as a general background would have every state dot checked against a hovered
        // button, which is not a combination that exists.
        foreach (var pair in contrast.GetProperty("pairs").EnumerateArray())
        {
            classified.Add(pair[0].GetString()!);
            classified.Add(pair[1].GetString()!);
        }

        var unclassified = Tokens.RootElement.GetProperty("color").EnumerateObject()
            .Select(c => c.Name)
            .Where(name => !classified.Contains(name))
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            $"these colours are in no contrast list, so nothing checks them: {string.Join(", ", unclassified)}");
    }

    [Fact]
    public void EveryColorHasBothThemesAndAUse()
    {
        foreach (var color in Tokens.RootElement.GetProperty("color").EnumerateObject())
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", color.Value.GetProperty("dark").GetString());
            Assert.Matches("^#[0-9A-Fa-f]{6}$", color.Value.GetProperty("light").GetString());
            Assert.False(string.IsNullOrWhiteSpace(color.Value.GetProperty("use").GetString()), $"{color.Name} has no use");
        }
    }

    [Fact]
    public void OneAccentOnly()
    {
        // Spec §2.1: "one accent only". Anything else that looks like an accent must be a state colour.
        var names = Tokens.RootElement.GetProperty("color").EnumerateObject().Select(c => c.Name).ToList();

        Assert.Equal(["accent.primary", "accent.primary.hover"], names.Where(n => n.StartsWith("accent.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("#000000", "#FFFFFF", 21.0)]
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]
    [InlineData("#777777", "#FFFFFF", 4.48)]
    public void ContrastFormulaMatchesWcag(string a, string b, double expected)
    {
        Assert.Equal(expected, ContrastRatio(a, b), precision: 2);
    }

    private static string Hex(string theme, string token) =>
        Tokens.RootElement.GetProperty("color").GetProperty(token).GetProperty(theme).GetString()!;

    /// <summary>WCAG 2.1 relative-luminance contrast ratio for two #RRGGBB colours.</summary>
    public static double ContrastRatio(string hexA, string hexB)
    {
        var la = Luminance(hexA);
        var lb = Luminance(hexB);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        var rgb = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var channel = int.Parse(hex.AsSpan(1 + (i * 2), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            rgb[i] = channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * rgb[0]) + (0.7152 * rgb[1]) + (0.0722 * rgb[2]);
    }
}
