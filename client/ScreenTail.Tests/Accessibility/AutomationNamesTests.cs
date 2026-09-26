using System.Text.RegularExpressions;

namespace ScreenTail.Tests.Accessibility;

/// <summary>
/// Spec §7: every interactive element has an automation name (ST-084). Read off the XAML rather than
/// off a running window, so it holds on the Mac and on every commit: an element is named by
/// <c>AutomationProperties.Name</c>, by a literal or bound <c>Content</c> (a button or a check box reads
/// its label), or by a <c>Header</c>. The token gallery is a design reference, not a screen a technician
/// reaches, and is left out. Narrator, focus order and hit targets need Windows and Accessibility
/// Insights; <c>docs/ux/a11y-audit.md</c> says which is which.
/// </summary>
public sealed partial class AutomationNamesTests
{
    private static readonly string[] Interactive =
    [
        "Button", "ToggleButton", "RepeatButton", "TextBox", "PasswordBox", "CheckBox", "RadioButton",
        "ComboBox", "Slider", "ListBox", "ListView", "DataGrid", "TabControl", "MenuItem", "Hyperlink",
    ];

    [GeneratedRegex(@"<(Button|ToggleButton|RepeatButton|TextBox|PasswordBox|CheckBox|RadioButton|ComboBox|Slider|ListBox|ListView|DataGrid|TabControl|MenuItem|Hyperlink)(?=[\s/>])(?<attrs>(?:[^>]|\n)*?)(?:/>|>)", RegexOptions.Multiline)]
    private static partial Regex Element();

    [Fact]
    public void EveryInteractiveElementHasAName()
    {
        var files = Xaml();
        Assert.NotEmpty(files);
        Assert.NotEmpty(Interactive);

        var unnamed = new List<string>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Element().Matches(source))
            {
                var attrs = match.Groups["attrs"].Value;
                var named = attrs.Contains("AutomationProperties.Name=", StringComparison.Ordinal)
                    || attrs.Contains("AutomationProperties.LabeledBy=", StringComparison.Ordinal)
                    || Regex.IsMatch(attrs, @"\bContent=""[^""]+""")
                    || Regex.IsMatch(attrs, @"\bHeader=""[^""]+""");
                if (!named)
                {
                    var line = source[..match.Index].Count(c => c == '\n') + 1;
                    unnamed.Add($"{Path.GetFileName(file)}:{line} <{match.Groups[1].Value}>");
                }
            }
        }

        Assert.Empty(unnamed);
    }

    private static IReadOnlyList<string> Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.UI");
            if (Directory.Exists(candidate))
            {
                return
                [
                    .. Directory.EnumerateFiles(candidate, "*.xaml", SearchOption.AllDirectories)
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Theme{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Gallery{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
                ];
            }

            directory = directory.Parent;
        }

        return [];
    }
}
