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
