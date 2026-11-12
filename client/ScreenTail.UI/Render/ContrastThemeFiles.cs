using System.Globalization;
using System.IO;

namespace ScreenTail.UI.Render;

/// <summary>
/// Prints the colours of the contrast themes Windows ships, so the fixed palettes can be checked against
/// the files on a real machine. Microsoft's documentation names each system colour's role but gives no
/// values. The <c>.theme</c> files under <c>%WINDIR%\Resources\Ease of Access Themes</c> are what Windows
/// applies, so they are the source. Harness only, and only printed: nothing reads the answer.
/// </summary>
internal static class ContrastThemeFiles
{
    /// <summary>The <c>[Control Panel\Colors]</c> names the palettes stand in for, by <c>SystemColors</c> name.</summary>
    private static readonly (string File, string System)[] Names =
    [
        ("Window", "Window"),
        ("WindowText", "WindowText"),
        ("WindowFrame", "WindowFrame"),
        ("ButtonFace", "Control"),
        ("ButtonText", "ControlText"),
        ("ActiveBorder", "ActiveBorder"),
        ("GrayText", "GrayText"),
        ("Hilight", "Highlight"),
        ("HilightText", "HighlightText"),
        ("HotTrackingColor", "HotTrack"),
    ];

    public static void Report()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Ease of Access Themes");
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"contrast themes: no {folder}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.theme").Order(StringComparer.OrdinalIgnoreCase))
        {
            var colours = Colours(File.ReadAllLines(file));
            var line = string.Join(", ", Names.Select(n => $"{n.System}={(colours.TryGetValue(n.File, out var hex) ? hex : "?")}"));
            Console.WriteLine($"contrast theme {Path.GetFileName(file)} ({Display(file)}): {line}");
        }
    }

    private static Dictionary<string, string> Colours(string[] lines)
    {
        var colours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inColours = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inColours = line.Equals(@"[Control Panel\Colors]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inColours || equals <= 0)
            {
                continue;
            }

            var parts = line[(equals + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = parts.Select(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? (byte?)b : null).ToArray();
            if (bytes.Length == 3 && bytes.All(b => b is not null))
            {
                colours[line[..equals].Trim()] = string.Create(CultureInfo.InvariantCulture, $"#{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}");
            }
        }

        return colours;
    }

    private static string Display(string file) =>
        File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase))?["DisplayName=".Length..] ?? "?";
}
