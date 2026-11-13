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

        // The floor comes from the artifact under test, so a token file that lowered it to 2.0 would pass
        // every failing colour. WCAG AA for body text is 4.5:1 and that is not the token's to change.
        Assert.True(minimum >= 4.5, $"contrast.minimum is {minimum}; WCAG AA body text needs 4.5:1");

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

    public static TheoryData<string, string, string> ControlEdgePairs
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            var contrast = Tokens.RootElement.GetProperty("contrast");
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var edge in contrast.GetProperty("control_edges").EnumerateArray())
                {
                    foreach (var background in contrast.GetProperty("control_edge_backgrounds").EnumerateArray())
                    {
                        data.Add(theme, edge.GetString()!, background.GetString()!);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ControlEdgePairs))]
    public void AnInputsEdgeCanBeSeenOnWhatItSitsOn(string theme, string edge, string background)
    {
        // WCAG 1.4.11 (ST-016): an input's fill is a near-white on a near-white card, so its outline is
        // the only thing that says where the box is. Inputs were outlined in border.strong, 1.51:1 to
        // 1.67:1 in light, which the structural test above let through because it asks only for "not
        // the same colour". The floor is checked here, not read from the file alone.
        var minimum = Tokens.RootElement.GetProperty("contrast").GetProperty("control_edge_minimum").GetDouble();
        Assert.True(minimum >= 3.0, $"contrast.control_edge_minimum is {minimum}; WCAG 1.4.11 needs 3:1");

        var ratio = ContrastRatio(Hex(theme, edge), Hex(theme, background));

        Assert.True(ratio >= minimum, $"{theme}: {edge} on {background} is {ratio:0.00}:1, below {minimum}:1");
    }

    public static TheoryData<string, string, string, string> ShellPairs
    {
        get
        {
            // Spec v0.6.0: the frame (rail, title bar, pill) is its own surface, indigo navy in light and a
            // navy-tinted dark in dark, so what is drawn on it is checked against it and not against the panes.
            var data = new TheoryData<string, string, string, string>();
            var contrast = Tokens.RootElement.GetProperty("contrast");
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var background in contrast.GetProperty("shell_backgrounds").EnumerateArray())
                {
                    foreach (var text in contrast.GetProperty("shell_text").EnumerateArray())
                    {
                        data.Add(theme, "minimum", text.GetString()!, background.GetString()!);
                    }

                    foreach (var indicator in contrast.GetProperty("shell_indicators").EnumerateArray())
                    {
                        data.Add(theme, "indicator_minimum", indicator.GetString()!, background.GetString()!);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ShellPairs))]
    public void WhatIsDrawnOnTheFrameIsReadableOnIt(string theme, string floor, string foreground, string background)
    {
        // Text on the frame meets the same 4.5:1 as text on a pane, and a state on the frame the same 3:1.
        // The floor is read from the file, so it is held here too.
        var minimum = Tokens.RootElement.GetProperty("contrast").GetProperty(floor).GetDouble();
        Assert.True(minimum >= (floor == "minimum" ? 4.5 : 3.0), $"contrast.{floor} is {minimum}");

        var ratio = ContrastRatio(Hex(theme, foreground), Hex(theme, background));

        Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, below {minimum}:1");
    }

    [Fact]
    public void TheFrameListsHoldTheWordsAndTheStates()
    {
        // The lists are in the file under test, so one that dropped shell.text would check nothing on the frame.
        var contrast = Tokens.RootElement.GetProperty("contrast");
        Assert.Contains("shell.text", Names(contrast, "shell_text"));
        Assert.Contains("shell.muted", Names(contrast, "shell_text"));
        Assert.Contains("shell.bg", Names(contrast, "shell_backgrounds"));
        Assert.Contains("shell.recording", Names(contrast, "shell_indicators"));
        Assert.Contains("shell.offline", Names(contrast, "shell_indicators"));
    }

    public static TheoryData<string, string, string, string> ShellDecorationPairs
    {
        get
        {
            var data = new TheoryData<string, string, string, string>();
            var contrast = Tokens.RootElement.GetProperty("contrast");
            foreach (var theme in new[] { "dark", "light" })
            {
                foreach (var list in new[] { "shell_structural", "shell_decorative" })
                {
                    foreach (var token in contrast.GetProperty(list).EnumerateArray())
                    {
                        foreach (var background in contrast.GetProperty("shell_backgrounds").EnumerateArray())
                        {
                            data.Add(theme, list, token.GetString()!, background.GetString()!);
                        }
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ShellDecorationPairs))]
    public void AFrameDividerOrDecorationIsNotTheSameColourAsTheFrame(string theme, string list, string token, string background)
    {
        // shell.line divides the rail, and the ribbon band meets the frame on the light pill cap. Neither is
        // ever the only cue (the pill has its own outline and words), so like `structural` they are held only
        // to drawing something. The band on the frame is the logo case, iris on navy at 2.80:1, deep by design.
        var ratio = ContrastRatio(Hex(theme, token), Hex(theme, background));

        Assert.True(ratio > 1.0, $"{theme}: {token} ({list}) is the same colour as {background}, so it draws nothing");
    }

    [Fact]
    public void ADecorationOnTheFrameIsStillAnIndicatorOnThePanes()
    {
        // Being decorative on the frame is not a way out of every check: the band sits on the selected row,
        // where it is a cue, so it stays in `indicators` and is held to 3:1 on every content background.
        var contrast = Tokens.RootElement.GetProperty("contrast");
        var indicators = Names(contrast, "indicators");
        foreach (var name in Names(contrast, "shell_decorative"))
        {
            Assert.Contains(name, indicators);
            Assert.DoesNotContain(name, Names(contrast, "shell_text"));
            Assert.DoesNotContain(name, Names(contrast, "shell_indicators"));
        }

        Assert.Contains("brand.ribbon.band", Names(contrast, "shell_decorative"));
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void TheBrandIsOneHue(string theme)
    {
        // Spec v0.6.0: one brand hue, Iris. "Act here" (buttons, links, focus, checked) follows the brand, so
        // the brand mark colour, the ribbon band and the accent are one value, not three that drift apart.
        Assert.Equal(Hex(theme, "accent.primary"), Hex(theme, "brand.signature"));
        Assert.Equal(Hex(theme, "accent.primary"), Hex(theme, "brand.ribbon.band"));
    }

    [Fact]
    public void TheBrandIsIris()
    {
        // The owner's pick (Spec v0.6.0): Iris #6A43C4, hover #572FA7, dark action #B2A6EC with ink text, frame #141030.
        Assert.Equal("#6A43C4", Hex("light", "accent.primary"));
        Assert.Equal("#572FA7", Hex("light", "accent.primary.hover"));
        Assert.Equal("#B2A6EC", Hex("dark", "accent.primary"));
        Assert.Equal("#0A071E", Hex("dark", "text.on-accent"));
        Assert.Equal("#141030", Hex("light", "shell.bg"));
    }

    [Theory]
    [InlineData("dark", "#7CCDD6")]
    [InlineData("light", "#3E6E7A")]
    public void OutOfScopeIsSlateTealNotTheBrandHue(string theme, string expected)
    {
        // Spec v0.6.0: the v0.5 violet was 4.6 from Iris in OKLab, so out of scope moved to slate teal. A state
        // that looks like the brand reads as "act here"; this catches it moving back.
        Assert.Equal(expected, Hex(theme, "state.scope"));
        Assert.NotEqual(Hex(theme, "accent.primary"), Hex(theme, "state.scope"));
    }

    [Fact]
    public void DangerIsHeldAsTextBecauseItIsWords()
    {
        // state.danger draws words, not only a cross: the error line under a field (Text.Error), the
        // Danger button's label and Review's Discard on hover. So it is in `text`, held to 4.5:1 on every
        // content fill, the tints included, and not only to an indicator's 3:1 (v0.6.12). It stays in
        // `indicators` for the cross and the outlines it also draws.
        var contrast = Tokens.RootElement.GetProperty("contrast");
        Assert.Contains("state.danger", Names(contrast, "text"));
        Assert.Contains("state.danger", Names(contrast, "indicators"));
        foreach (var theme in new[] { "dark", "light" })
        {
            foreach (var background in Names(contrast, "backgrounds"))
            {
                var ratio = ContrastRatio(Hex(theme, "state.danger"), Hex(theme, background));
                Assert.True(ratio >= 4.5, $"state.danger on {background} ({theme}) is {ratio:F2}:1");
            }
        }
    }

    private static List<string> Names(JsonElement contrast, string list) =>
        contrast.GetProperty(list).EnumerateArray().Select(n => n.GetString()!).ToList();

    [Fact]
    public void TheControlEdgeListHoldsTheSurfacesTheReviewMeasured()
    {
        // The list is in the file under test, so a change that dropped bg.surface from it would pass
        // every edge. The two surfaces the review measured are held here.
        var backgrounds = Tokens.RootElement.GetProperty("contrast").GetProperty("control_edge_backgrounds")
            .EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains("bg.base", backgrounds);
        Assert.Contains("bg.surface", backgrounds);

        var edges = Tokens.RootElement.GetProperty("contrast").GetProperty("control_edges")
            .EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains("border.control", edges);
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
        foreach (var list in new[] { "text", "backgrounds", "indicators", "structural", "control_edges", "shell_text", "shell_backgrounds", "shell_indicators", "shell_structural", "shell_decorative" })
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
