using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ScreenTail.Tests.Theme;

/// <summary>
/// ST-016: a keyed style is only applied to an element its <c>TargetType</c> allows. WPF throws
/// <c>InvalidOperationException</c> the moment such an element is built ("'TextBlock' TargetType does not
/// match type of element 'TextBox'"), so a mismatch in a dialog nobody opens in a test is a crash waiting
/// for the first technician who opens it. The typed confirmation window shipped that way from #86 until
/// #228. Read off the XAML, so it holds on the Mac and on every commit, for every screen at once.
/// <para>
/// WPF's own types are not loadable here (this suite is net10.0, not Windows), so their base classes are
/// written down below, and the app's own controls are read from <c>class X : Y</c> in ScreenTail.UI. An
/// element type that is in neither fails the test and asks to be added, rather than being let through.
/// </para>
/// </summary>
public sealed partial class StyleTargetTypeTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The WPF classes the views use, each with its direct base, up to FrameworkElement.</summary>
    private static readonly Dictionary<string, string> WpfBase = new(StringComparer.Ordinal)
    {
        ["FrameworkElement"] = "UIElement",
        ["Control"] = "FrameworkElement",
        ["ContentControl"] = "Control",
        ["HeaderedContentControl"] = "ContentControl",
        ["ItemsControl"] = "Control",
        ["HeaderedItemsControl"] = "ItemsControl",
        ["Selector"] = "ItemsControl",
        ["MultiSelector"] = "Selector",
        ["RangeBase"] = "Control",
        ["ButtonBase"] = "ContentControl",
        ["TextBoxBase"] = "Control",
        ["Decorator"] = "FrameworkElement",
        ["Panel"] = "FrameworkElement",
        ["Shape"] = "FrameworkElement",
        ["Window"] = "ContentControl",
        ["UserControl"] = "ContentControl",
        ["Label"] = "ContentControl",
        ["ToolTip"] = "ContentControl",
        ["ScrollViewer"] = "ContentControl",
        ["ListBoxItem"] = "ContentControl",
        ["Expander"] = "HeaderedContentControl",
        ["Button"] = "ButtonBase",
        ["RepeatButton"] = "ButtonBase",
        ["ToggleButton"] = "ButtonBase",
        ["CheckBox"] = "ToggleButton",
        ["RadioButton"] = "ToggleButton",
        ["TextBox"] = "TextBoxBase",
        ["PasswordBox"] = "Control",
        ["Thumb"] = "Control",
        ["GridSplitter"] = "Thumb",
        ["ProgressBar"] = "RangeBase",
        ["Slider"] = "RangeBase",
        ["ScrollBar"] = "RangeBase",
        ["ListBox"] = "Selector",
        ["ListView"] = "ListBox",
        ["ComboBox"] = "Selector",
        ["TabControl"] = "Selector",
        ["DataGrid"] = "MultiSelector",
        ["MenuItem"] = "HeaderedItemsControl",
        ["TextBlock"] = "FrameworkElement",
        ["Image"] = "FrameworkElement",
        ["Border"] = "Decorator",
        ["Viewbox"] = "Decorator",
        ["StackPanel"] = "Panel",
        ["Grid"] = "Panel",
        ["DockPanel"] = "Panel",
        ["WrapPanel"] = "Panel",
        ["Canvas"] = "Panel",
        ["Ellipse"] = "Shape",
        ["Rectangle"] = "Shape",
        ["Path"] = "Shape",
        ["Line"] = "Shape",
    };

    [GeneratedRegex(@"^\{(?:StaticResource|DynamicResource)\s+(?<key>[^}\s]+)\s*\}$")]
    private static partial Regex ResourceReference();

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)\s*:\s*(?<base>[\w.]+)")]
    private static partial Regex ClassDeclaration();

    [Fact]
    public void EveryKeyedStyleIsAppliedOnlyToAnElementItsTargetTypeAllows()
    {
        var files = UiFiles("*.xaml");
        Assert.NotEmpty(files);

        var documents = files.Select(f => (file: f, xml: XDocument.Load(f, LoadOptions.SetLineInfo))).ToList();
        var targets = StyleTargets(documents);
        Assert.Contains("Button.Danger", targets.Keys);

        var bases = Bases();
        var wrong = new List<string>();
        var checkedCount = 0;
        foreach (var (file, xml) in documents)
        {
            foreach (var element in xml.Descendants())
            {
                var style = element.Attribute("Style")?.Value;
                if (style is null)
                {
                    continue;
                }

                var match = ResourceReference().Match(style);
                if (!match.Success)
                {
                    continue;
                }

                var key = match.Groups["key"].Value;
                var at = $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}";
                if (!targets.TryGetValue(key, out var target))
                {
                    wrong.Add($"{at} <{element.Name.LocalName}> uses {key}, which no XAML file in ScreenTail.UI defines");
                    continue;
                }

                checkedCount++;
                if (target is null)
                {
                    continue;
                }

                var type = element.Name.LocalName;
                if (!bases.ContainsKey(type))
                {
                    wrong.Add($"{at} <{type}> is a type this test does not know the base of: add it to WpfBase");
                }
                else if (!IsOrDerivesFrom(type, target, bases))
                {
                    wrong.Add($"{at} <{type}> uses {key}, whose TargetType is {target}");
                }
            }
        }

        Assert.True(checkedCount > 100, $"only {checkedCount} styled elements were found; the scan is not reading the views");
        Assert.True(wrong.Count == 0, "a style for another type throws when the element is built:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void EveryBasedOnStyleIsForTheSameTypeOrABaseOfIt()
    {
        // The same exception, one step removed: a style BasedOn a style for a type that is not a base of
        // its own TargetType throws when the derived style is first used.
        var documents = UiFiles("*.xaml").Select(f => (file: f, xml: XDocument.Load(f, LoadOptions.SetLineInfo))).ToList();
        var targets = StyleTargets(documents);
        var bases = Bases();
        var wrong = new List<string>();
        foreach (var (file, xml) in documents)
        {
            foreach (var style in xml.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var basedOn = style.Attribute("BasedOn")?.Value;
                var own = TypeName(style.Attribute("TargetType")?.Value);
                var match = basedOn is null ? null : ResourceReference().Match(basedOn);
                if (match is null || !match.Success || own is null)
                {
                    continue;
                }

                var key = match.Groups["key"].Value;
                var at = $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)style).LineNumber}";
                if (targets.TryGetValue(key, out var parent) && parent is not null && !IsOrDerivesFrom(own, parent, bases))
                {
                    wrong.Add($"{at} a {own} style is BasedOn {key}, whose TargetType is {parent}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void TheCheckCatchesATextBlockStyleOnATextBox()
    {
        // The defect this test exists for, so a change to the walk that stops it seeing one fails here.
        var bases = Bases();

        Assert.False(IsOrDerivesFrom("TextBox", "TextBlock", bases));
        Assert.True(IsOrDerivesFrom("TextBox", "TextBox", bases));
        Assert.True(IsOrDerivesFrom("GridSplitter", "Thumb", bases));
        Assert.True(IsOrDerivesFrom("Glyph", "TextBlock", bases));
        Assert.True(IsOrDerivesFrom("PageButton", "ButtonBase", bases));
        Assert.False(IsOrDerivesFrom("PageButton", "Button", bases));
    }

    /// <summary>Every keyed style in the views, with its TargetType's class name (null if it has none).</summary>
    private static Dictionary<string, string?> StyleTargets(IEnumerable<(string file, XDocument xml)> documents)
    {
        var targets = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (_, xml) in documents)
        {
            foreach (var style in xml.Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var key = style.Attribute(X + "Key")?.Value;
                if (key is not null)
                {
                    // Keys are unique across the views today; if one is ever reused, the first wins here
                    // and the second file's elements are checked against it, which errs towards failing.
                    targets.TryAdd(key, TypeName(style.Attribute("TargetType")?.Value));
                }
            }
        }

        return targets;
    }

    /// <summary><c>Button</c>, <c>controls:Glyph</c> and <c>{x:Type Button}</c> all name the class Button or Glyph.</summary>
    private static string? TypeName(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return null;
        }

        var name = targetType.Trim();
        if (name.StartsWith("{x:Type", StringComparison.Ordinal))
        {
            name = name["{x:Type".Length..].TrimEnd('}').Trim();
        }

        var colon = name.LastIndexOf(':');
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    /// <summary>WPF's classes plus the app's own, read from the C# that declares them.</summary>
    private static Dictionary<string, string> Bases()
    {
        var bases = new Dictionary<string, string>(WpfBase, StringComparer.Ordinal);
        foreach (var file in UiFiles("*.cs"))
        {
            foreach (Match match in ClassDeclaration().Matches(File.ReadAllText(file)))
            {
                var baseName = match.Groups["base"].Value;
                baseName = baseName[(baseName.LastIndexOf('.') + 1)..];
                if (WpfBase.ContainsKey(baseName))
                {
                    bases.TryAdd(match.Groups["name"].Value, baseName);
                }
            }
        }

        return bases;
    }

    private static bool IsOrDerivesFrom(string type, string target, Dictionary<string, string> bases)
    {
        for (var current = type; current is not null; current = bases.GetValueOrDefault(current))
        {
            if (current == target)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> UiFiles(string pattern)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI");
            if (Directory.Exists(candidate))
            {
                var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
                var bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
                return
                [
                    .. Directory.EnumerateFiles(candidate, pattern, SearchOption.AllDirectories)
                        .Where(f => !f.Contains(obj, StringComparison.Ordinal) && !f.Contains(bin, StringComparison.Ordinal)),
                ];
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("client/ScreenTail.UI was not found above the test output directory");
    }
}
