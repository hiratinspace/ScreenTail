using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace ScreenTail.UI.Render;

/// <summary>
/// The <c>index.html</c> the harness writes beside its renders: every PNG in the folder, grouped by area,
/// then screen, then theme, so the artifact can be reviewed screen by screen with the themes side by side
/// rather than as two hundred files in alphabetical order.
///
/// Each harness process rewrites it from what is in the folder when it finishes, so the last one to run
/// leaves an index of all of them. No WPF types, so the grouping is tested on any OS.
/// </summary>
internal static class RenderIndex
{
    public const string FileName = "index.html";

    /// <summary>
    /// A render's place in the index. The area is the first segment of the name (<c>shell</c>,
    /// <c>settings</c>, <c>hud</c>), the screen everything before the theme, and the theme the last
    /// segment when it is one the harness renders. A name with no known theme is its own screen.
    /// </summary>
    public static (string Area, string Screen, string? Theme) Parse(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var area = stem.Split('-')[0];
        var cut = stem.LastIndexOf('-');
        if (cut > 0)
        {
            var last = stem[(cut + 1)..];
            if (RenderThemes.All.Any(theme => theme.Suffix() == last))
            {
                return (area, stem[..cut], last);
            }
        }

        return (area, stem, null);
    }

    /// <summary>The page for these file names. Order in, order irrelevant: the page sorts.</summary>
    public static string Build(IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var files = fileNames
            .Where(name => name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Place: Parse(name)))
            .ToList();

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><title>ScreenTail renders</title>");
        html.AppendLine("<style>body{font:14px/1.4 'Segoe UI',sans-serif;margin:24px;background:#f3f3f3;color:#1b1b1b}"
            + "h2{margin-top:40px;border-bottom:1px solid #ccc}h3{margin:24px 0 8px;font-size:15px}"
            + ".row{display:flex;flex-wrap:wrap;gap:12px;align-items:flex-start}"
            + "figure{margin:0;max-width:420px}figure img{max-width:100%;border:1px solid #999;background:repeating-conic-gradient(#ddd 0 25%,#fff 0 50%) 0 0/16px 16px}"
            + "figcaption{font-size:12px;color:#555}</style></head><body>");
        html.AppendLine(Inv($"<h1>ScreenTail renders</h1><p>{files.Count} images. Grouped by area, then screen, then theme (dark, light, high contrast black, high contrast white). Click an image for full size.</p>"));

        var areas = files.GroupBy(f => f.Place.Area, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        html.Append("<p>");
        html.AppendJoin(" · ", areas.Select(a => Inv($"<a href=\"#{Escape(a.Key)}\">{Escape(a.Key)}</a> ({a.Count()})")));
        html.AppendLine("</p>");

        foreach (var area in areas)
        {
            html.AppendLine(Inv($"<h2 id=\"{Escape(area.Key)}\">{Escape(area.Key)}</h2>"));
            foreach (var screen in area.GroupBy(f => f.Place.Screen, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                html.AppendLine(Inv($"<h3>{Escape(screen.Key)}</h3><div class=\"row\">"));
                foreach (var (name, place) in screen.OrderBy(f => Order(f.Place.Theme)).ThenBy(f => f.Name, StringComparer.Ordinal))
                {
                    var href = Uri.EscapeDataString(name);
                    html.AppendLine(Inv($"<figure><a href=\"{href}\"><img src=\"{href}\" alt=\"{Escape(name)}\" loading=\"lazy\"></a><figcaption>{Escape(place.Theme ?? name)}</figcaption></figure>"));
                }

                html.AppendLine("</div>");
            }
        }

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    /// <summary>Writes the index for every PNG in the folder.</summary>
    public static void Write(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var names = Directory.EnumerateFiles(directory, "*.png").Select(Path.GetFileName).OfType<string>();
        File.WriteAllText(Path.Combine(directory, FileName), Build(names));
    }

    private static int Order(string? theme)
    {
        for (var i = 0; i < RenderThemes.All.Count; i++)
        {
            if (RenderThemes.All[i].Suffix() == theme)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
