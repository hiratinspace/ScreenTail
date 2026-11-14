using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ScreenTail.Tests.Theme;

/// <summary>
/// ST-016: the views take colours and sizes from the design tokens, not from literals. A literal colour
/// does not follow the theme (high contrast maps every <c>Brush.*</c> token to a system colour, a literal
/// stays what it is), and a literal size drifts off the spacing scale one screen at a time. Read off the
/// XAML, so it holds on the Mac and on every commit.
/// <para>
/// <b>It is a ratchet.</b> The literals on main when this test was written are recorded in
/// <c>XamlLiteralBaseline.txt</c> next to this file, one line per file, kind and value with a count. A file
/// that gains a literal, or a count that goes up, fails. A count that goes down also fails, asking for the
/// baseline to be lowered, so the baseline only shrinks. To rewrite the baseline from the XAML as it is now,
/// run this class with <c>SCREENTAIL_XAML_BASELINE=update</c> set (see <see cref="UpdateCommand"/>) and
/// commit the file. Update mode is refused on CI, so CI never rewrites it.
/// </para>
/// <para><b>What is scanned.</b> Every <c>.xaml</c> under <c>client/ScreenTail.UI</c>, Components.xaml
/// included, except <c>Theme/Tokens.*.xaml</c>, which define the values. Attributes and <c>Setter</c>
/// values; anything in braces (a resource, binding or <c>x:Static</c>) is not a literal.</para>
/// <para><b>The rules.</b></para>
/// <list type="bullet">
/// <item><b>Colour.</b> On a colour or brush property (<c>Background</c>, <c>Foreground</c>, <c>BorderBrush</c>,
/// <c>Fill</c>, <c>Stroke</c>, <c>Color</c>, <c>OpacityMask</c>, anything ending in <c>Brush</c> or <c>Color</c>):
/// any <c>#RGB</c>, <c>#ARGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c> value and any named colour (<c>White</c>,
/// <c>Red</c>, ...). <c>Transparent</c> is allowed: it is "no fill", not a colour, and is the same in every
/// theme. Use a <c>Brush.*</c> token.</item>
/// <item><b>Margin and Padding.</b> Each side must be 0 or on the spacing scale (<c>Space.*</c>: 4, 8, 12, 16,
/// 24, 32, 40), sign ignored. WPF has no Thickness token per side, so an on-scale literal such as
/// <c>0,8,0,0</c> is how the scale is written and is allowed; an off-scale side (6, 10, 14, 20, ...) is drift.
/// A <c>Padding</c> equal to a <c>Padding.*</c> token (24, 20, or 14,5) must use the token.</item>
/// <item><b>CornerRadius.</b> Only 0. Every other radius is a <c>Radius.*</c> token (6 control, 8 card, 22 pill).</item>
/// <item><b>FontSize.</b> Never a literal: the type scale is <c>Size.*</c> (11, 12, 14, 16, 24).</item>
/// <item><b>BorderThickness.</b> Each side 0 or 1 (a hairline). Thicker edges (the focus ring, the 4 px
/// ribbon band) belong in a component style or a token.</item>
/// <item><b>Width, Height, Min* and Max*.</b> Only a literal equal to a <c>Layout.*</c> or <c>Icon.*</c> size
/// (row 36, control 32, HUD 44, hit target 28, content 760, envelope label 52, narrow field 72, side column
/// and pane minimum 300, icons 16 and 20) fails, naming the token. Other widths and heights (window sizes,
/// column widths, glyph boxes) have no token, so a rule there would be noise; <c>*</c> and <c>Auto</c> are
/// not numbers and are never flagged.</item>
/// </list>
/// <para>The baseline counts by value rather than by line, so an edit that moves lines does not touch it,
/// and a fix in one place cannot hide a new literal of another value in the same file.</para>
/// </summary>
public sealed partial class XamlLiteralRatchetTests
{
    private const string BaselineFile = "XamlLiteralBaseline.txt";
    private const string UpdateVariable = "SCREENTAIL_XAML_BASELINE";

    /// <summary>The one step that rewrites the baseline, quoted in every failure.</summary>
    internal const string UpdateCommand =
        "SCREENTAIL_XAML_BASELINE=update ~/.dotnet/dotnet run --project client/ScreenTail.Tests -- -class ScreenTail.Tests.Theme.XamlLiteralRatchetTests"
        + " (PowerShell: $env:SCREENTAIL_XAML_BASELINE='update'; dotnet run --project client/ScreenTail.Tests -- -class ScreenTail.Tests.Theme.XamlLiteralRatchetTests),"
        + " then commit client/ScreenTail.Tests/Theme/" + BaselineFile;

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> ColourProperties = new(StringComparer.Ordinal)
    {
        "Background", "Foreground", "BorderBrush", "Fill", "Stroke", "Color", "OpacityMask",
    };

    private static readonly HashSet<string> BoxProperties = new(StringComparer.Ordinal)
    {
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
    };

    [GeneratedRegex(@"^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"^[A-Za-z]+$")]
    private static partial Regex NamedColour();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex ThicknessSeparator();

    /// <summary>One literal where a token belongs. <c>Kind</c> is the property, or <c>Colour</c> for every colour property.</summary>
    internal sealed record Literal(string File, int Line, string Kind, string Property, string Value, string Hint)
    {
        public (string File, string Kind, string Value) Key => (File, Kind, Value);

        public override string ToString() => $"{File}:{Line} {Property}=\"{Value}\": {Hint}";
    }

    [Fact]
    public void NoXamlFileGainsALiteralColourOrSize()
    {
        var current = Current();
        if (UpdateRequested())
        {
            WriteBaseline(current);
            return;
        }

        var baseline = ReadBaseline();
        var grown = new List<string>();
        foreach (var group in current.GroupBy(l => l.Key).OrderBy(g => g.Key))
        {
            var allowed = baseline.GetValueOrDefault(group.Key);
            var count = group.Count();
            if (count <= allowed)
            {
                continue;
            }

            var note = allowed == 0 ? string.Empty : $" [{count} in this file now, baseline {allowed}: one of these is new]";
            grown.AddRange(group.Select(l => l + note));
        }

        Assert.True(
            grown.Count == 0,
            "a view gained a literal where a design token belongs; use the token named, or, if the literal is"
            + " deliberate (another PR's baseline is being merged, say), rewrite the baseline with: " + UpdateCommand
            + "\n" + string.Join("\n", grown));
    }

    [Fact]
    public void TheBaselineIsLoweredWhenALiteralIsRemoved()
    {
        var current = Current();
        if (UpdateRequested())
        {
            WriteBaseline(current);
            return;
        }

        var counts = current.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.Count());
        var stale = new List<string>();
        foreach (var (key, allowed) in ReadBaseline().OrderBy(e => e.Key))
        {
            var count = counts.GetValueOrDefault(key);
            if (count < allowed)
            {
                stale.Add($"{key.File} {key.Kind}=\"{key.Value}\": baseline {allowed}, now {count}");
            }
        }

        Assert.True(
            stale.Count == 0,
            "literals were removed (thank you); lower the baseline so they cannot come back, with: " + UpdateCommand
            + "\n" + string.Join("\n", stale));
    }

    [Fact]
    public void TheScanReadsTheViewsAndTheTokens()
    {
        // A scan that silently finds nothing would pass the ratchet forever.
        var tokens = Tokens.Load(UiRoot());
        Assert.Contains(8.0, tokens.Space);
        Assert.Contains("Radius.card", tokens.Radius.Keys);
        Assert.Contains("Size.md", tokens.FontSize.Keys);
        Assert.Contains("Layout.row-height", tokens.Box.Keys);

        var files = ViewFiles();
        Assert.Contains(files, f => f.EndsWith("ReviewPane.xaml", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("Components.xaml", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => Path.GetFileName(f).StartsWith("Tokens.", StringComparison.Ordinal));
        Assert.True(Current().Count > 50, "the scan found almost no literals; it is not reading the views");
    }

    [Fact]
    public void TheRulesCatchDriftAndLetTheScaleThrough()
    {
        var tokens = Tokens.Load(UiRoot());
        string? Kind(string property, string value) => Classify("F.xaml", 1, property, value, tokens)?.Kind;
        string? Hint(string property, string value) => Classify("F.xaml", 1, property, value, tokens)?.Hint;

        Assert.Equal("Colour", Kind("Background", "#FFF"));
        Assert.Equal("Colour", Kind("Fill", "#60000000"));
        Assert.Equal("Colour", Kind("Foreground", "White"));
        Assert.Equal("Colour", Kind("SelectionBrush", "Red"));
        Assert.Null(Kind("Background", "Transparent"));
        Assert.Null(Kind("Background", "{DynamicResource Brush.bg.surface}"));
        Assert.Null(Kind("Text", "#1"));

        Assert.Null(Kind("Margin", "0,8,0,0"));
        Assert.Null(Kind("Margin", "-8,0,0,0"));
        Assert.Null(Kind("Margin", "0 4 0 0"));
        Assert.Equal("Margin", Kind("Margin", "0,6,0,0"));
        Assert.Contains("Space.*", Hint("Margin", "0,10,0,0"), StringComparison.Ordinal);
        Assert.Null(Kind("Padding", "12,0"));
        Assert.Contains("Padding.card", Hint("Padding", "16"), StringComparison.Ordinal);
        Assert.Contains("Padding.control", Hint("Padding", "14,5"), StringComparison.Ordinal);
        Assert.Contains("Padding.pane", Hint("Padding", "24,24,24,24"), StringComparison.Ordinal);

        Assert.Null(Kind("CornerRadius", "0"));
        Assert.Contains("Radius.control", Hint("CornerRadius", "6"), StringComparison.Ordinal);
        Assert.Equal("CornerRadius", Kind("CornerRadius", "3"));

        Assert.Contains("Size.md", Hint("FontSize", "14"), StringComparison.Ordinal);
        Assert.Equal("FontSize", Kind("FontSize", "13"));

        Assert.Null(Kind("BorderThickness", "1"));
        Assert.Null(Kind("BorderThickness", "0,1,0,0"));
        Assert.Equal("BorderThickness", Kind("BorderThickness", "2"));

        Assert.Contains("Layout.row-height", Hint("Height", "32"), StringComparison.Ordinal);
        Assert.Contains("Icon.control", Hint("Width", "16"), StringComparison.Ordinal);
        Assert.Contains("Layout.content-max-width", Hint("MaxWidth", "760"), StringComparison.Ordinal);
        Assert.Null(Kind("Width", "160"));
        Assert.Null(Kind("Width", "*"));
        Assert.Null(Kind("Width", "Auto"));
        Assert.Null(Kind("Width", "NaN"));
    }

    /// <summary>Every literal in the views now.</summary>
    private static List<Literal> Current()
    {
        var root = UiRoot();
        var tokens = Tokens.Load(root);
        var found = new List<Literal>();
        foreach (var file in ViewFiles())
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var xml = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in xml.Descendants())
            {
                if (element.Name.LocalName == "Setter")
                {
                    var property = element.Attribute("Property")?.Value;
                    var value = element.Attribute("Value");
                    if (property is not null && value is not null)
                    {
                        Add(Classify(relative, Line(value), property, value.Value, tokens));
                    }

                    continue;
                }

                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace == X)
                    {
                        continue;
                    }

                    Add(Classify(relative, Line(attribute), attribute.Name.LocalName, attribute.Value, tokens));
                }
            }
        }

        return found;

        void Add(Literal? literal)
        {
            if (literal is not null)
            {
                found.Add(literal);
            }
        }
    }

    /// <summary>The literal this property and value are, under the rules in the class comment, or null.</summary>
    internal static Literal? Classify(string file, int line, string property, string value, Tokens tokens)
    {
        var name = property[(property.LastIndexOf('.') + 1)..];
        var text = value.Trim();
        if (text.Length == 0 || text.StartsWith('{'))
        {
            return null;
        }

        if (ColourProperties.Contains(name) || name.EndsWith("Brush", StringComparison.Ordinal) || name.EndsWith("Color", StringComparison.Ordinal))
        {
            var literal = HexColour().IsMatch(text) || (NamedColour().IsMatch(text) && text != "Transparent");
            return literal
                ? new Literal(file, line, "Colour", name, text, "a literal colour does not follow the theme or high contrast; use a Brush.* token ({DynamicResource Brush.name})")
                : null;
        }

        if (name is not ("Margin" or "Padding" or "CornerRadius" or "FontSize" or "BorderThickness") && !BoxProperties.Contains(name))
        {
            return null;
        }

        var numbers = Numbers(text);
        if (numbers is null)
        {
            return null;
        }

        string? hint = name switch
        {
            "Padding" when tokens.MatchThickness(numbers) is { } key =>
                $"this is {key}; use {{StaticResource {key}}}",
            "Margin" or "Padding" when numbers.Any(n => n != 0 && !tokens.Space.Contains(Math.Abs(n))) =>
                $"off the spacing scale; round each side to 0 or a Space.* value ({Join(tokens.Space)})"
                + (name == "Padding" ? ", or use a Padding.* token" : string.Empty),
            "CornerRadius" when numbers.Any(n => n != 0) =>
                tokens.Radius.FirstOrDefault(r => numbers.All(n => n == r.Value)).Key is { } key
                    ? $"this is {key}; use {{StaticResource {key}}}"
                    : $"no radius token has this value; use a Radius.* token ({string.Join(", ", tokens.Radius.Select(r => $"{r.Key} {Format(r.Value)}"))})",
            "FontSize" =>
                tokens.FontSize.FirstOrDefault(s => s.Value == numbers[0]).Key is { } key
                    ? $"this is {key}; use {{StaticResource {key}}}, or a Text.* style"
                    : $"off the type scale; use a Size.* token ({string.Join(", ", tokens.FontSize.Select(s => $"{s.Key} {Format(s.Value)}"))}) or a Text.* style",
            "BorderThickness" when numbers.Any(n => n is not (0 or 1)) =>
                "a border is 0 or a 1 px hairline; a thicker edge (focus ring, ribbon band) belongs in a component style using Focus.width or a token",
            _ when BoxProperties.Contains(name) && numbers.Length == 1 && tokens.Box.Where(b => b.Value == numbers[0]).Select(b => b.Key).ToList() is { Count: > 0 } keys =>
                $"this is {string.Join(" or ", keys)}; use {{StaticResource {keys[0]}}}" + (keys.Count > 1 ? " (whichever it means)" : string.Empty),
            _ => null,
        };

        return hint is null ? null : new Literal(file, line, name, name, text, hint);
    }

    /// <summary>The numbers in a size literal, or null if it is not all numbers (<c>Auto</c>, <c>*</c>, <c>NaN</c>).</summary>
    private static double[]? Numbers(string text)
    {
        var parts = ThicknessSeparator().Split(text).Where(p => p.Length > 0).ToArray();
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return numbers.Length > 0 ? numbers : null;
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Join(IEnumerable<double> values) => string.Join(", ", values.Select(Format));

    private static int Line(XAttribute attribute) => ((IXmlLineInfo)attribute).LineNumber;

    private static bool UpdateRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "update", StringComparison.Ordinal))
        {
            return false;
        }

        Assert.False(
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal),
            $"{UpdateVariable}=update rewrites the checked-in baseline; run it in a local checkout, not on CI");
        return true;
    }

    private static string BaselinePath()
    {
        var root = Path.GetDirectoryName(UiRoot())!;
        return Path.Combine(root, "ScreenTail.Tests", "Theme", BaselineFile);
    }

    private static Dictionary<(string File, string Kind, string Value), int> ReadBaseline()
    {
        var path = BaselinePath();
        Assert.True(File.Exists(path), $"{path} is missing; create it with: {UpdateCommand}");
        var baseline = new Dictionary<(string, string, string), int>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split('\t');
            Assert.True(
                fields.Length == 4 && int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out _),
                $"{BaselineFile}: \"{line}\" is not file<TAB>kind<TAB>value<TAB>count; rewrite it with: {UpdateCommand}");
            baseline[(fields[0], fields[1], fields[2])] = int.Parse(fields[3], CultureInfo.InvariantCulture);
        }

        return baseline;
    }

    private static void WriteBaseline(List<Literal> current)
    {
        var text = new StringBuilder();
        text.Append("// Literal colours and sizes in ScreenTail.UI's XAML where a design token belongs (ST-016).\n");
        text.Append("// Read by XamlLiteralRatchetTests, which fails if any count goes up or down. Do not edit by hand;\n");
        text.Append("// rewrite it with SCREENTAIL_XAML_BASELINE=update (see the test's doc comment). file, kind, value, count.\n");
        foreach (var group in current.GroupBy(l => l.Key).OrderBy(g => g.Key.File, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.Value, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"{group.Key.File}\t{group.Key.Kind}\t{group.Key.Value}\t{group.Count()}\n");
        }

        File.WriteAllText(BaselinePath(), text.ToString());
    }

    private static List<string> ViewFiles()
    {
        var root = UiRoot();
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var tokens = Path.Combine(root, "Theme", "Tokens.");
        return
        [
            .. Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains(obj, StringComparison.Ordinal) && !f.Contains(bin, StringComparison.Ordinal))
                .Where(f => !f.StartsWith(tokens, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];
    }

    private static string UiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("client/ScreenTail.UI was not found above the test output directory");
    }

    /// <summary>The size tokens, read from the generated Tokens.Common.g.xaml so the rules follow tokens.json.</summary>
    internal sealed class Tokens
    {
        public required SortedSet<double> Space { get; init; }

        public required Dictionary<string, double> Radius { get; init; }

        public required Dictionary<string, double> FontSize { get; init; }

        /// <summary><c>Layout.*</c> (not the paddings, which are Thickness tokens too) and <c>Icon.*</c>.</summary>
        public required Dictionary<string, double> Box { get; init; }

        public required Dictionary<string, double[]> Padding { get; init; }

        public static Tokens Load(string uiRoot)
        {
            var xml = XDocument.Load(Path.Combine(uiRoot, "Theme", "Tokens.Common.g.xaml"));
            var space = new SortedSet<double>();
            var radius = new Dictionary<string, double>(StringComparer.Ordinal);
            var fontSize = new Dictionary<string, double>(StringComparer.Ordinal);
            var box = new Dictionary<string, double>(StringComparer.Ordinal);
            var padding = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var element in xml.Root!.Elements())
            {
                var key = element.Attribute(X + "Key")?.Value;
                var numbers = key is null ? null : Numbers(element.Value);
                if (key is null || numbers is null)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "Double" when key.StartsWith("Space.", StringComparison.Ordinal):
                        space.Add(numbers[0]);
                        break;
                    case "Double" when key.StartsWith("Size.", StringComparison.Ordinal):
                        fontSize[key] = numbers[0];
                        break;
                    case "Double" when key.StartsWith("Icon.", StringComparison.Ordinal)
                        || (key.StartsWith("Layout.", StringComparison.Ordinal) && !key.Contains("padding", StringComparison.Ordinal)):
                        box[key] = numbers[0];
                        break;
                    case "CornerRadius":
                        radius[key] = numbers[0];
                        break;
                    case "Thickness":
                        padding[key] = Sides(numbers);
                        break;
                    default:
                        break;
                }
            }

            return new Tokens { Space = space, Radius = radius, FontSize = fontSize, Box = box, Padding = padding };
        }

        /// <summary>The Thickness token this literal spells out, if any.</summary>
        public string? MatchThickness(double[] numbers)
        {
            var sides = Sides(numbers);
            return Padding.FirstOrDefault(p => p.Value.SequenceEqual(sides)).Key;
        }

        /// <summary>XAML's 1, 2 or 4 value Thickness as left, top, right, bottom.</summary>
        private static double[] Sides(double[] n) => n.Length switch
        {
            1 => [n[0], n[0], n[0], n[0]],
            2 => [n[0], n[1], n[0], n[1]],
            4 => n,
            _ => [.. n],
        };
    }
}
