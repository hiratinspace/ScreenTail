using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScreenTail.Api.Summarize;

/// <summary>
/// How <see cref="DraftValidator"/> reads a draft's text before any rule looks at it (ST-061). The same
/// steps are <c>checks.read</c> in <c>research/prompts/checks.py</c>, over the same data.
///
/// As read: every lone surrogate becomes U+FFFD, the text is put in NFKC, every format character is
/// removed, every Unicode space and separator and U+001C to U+001F becomes a plain space, and a run of
/// spaces becomes one. That is the text as a person sees it on screen, so "In&lt;ZWSP&gt;stall",
/// "api&#160;&#160;key" and fullwidth letters read as what they show.
///
/// NFKC comes from <c>research/prompts/unicode-tables.json</c>, not from <see cref="string.Normalize()"/>.
/// The API runs with InvariantGlobalization, where Normalize hands non-ASCII text back unchanged, so a
/// validator built on it would normalise in the tests and do nothing in production. And a table both
/// validators read gives the same answer in both whatever Unicode version each platform has.
/// </summary>
internal static class DraftReading
{
    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    private static readonly Dictionary<int, int[]> Decompositions = [];
    private static readonly Dictionary<int, int> Combining = [];
    private static readonly Dictionary<long, int> Compositions = [];
    private static readonly HashSet<int> Unseen = [];
    private static readonly HashSet<int> Spaces = [];

    /// <summary>The Unicode version the NFKC data is from.</summary>
    public static string UnicodeVersion { get; }

    /// <summary>The code points that version assigns. Read only by the tests.</summary>
    public static IReadOnlySet<int> Assigned { get; }

    /// <summary>What .NET's Regex reads as white space (<c>\s</c>), which is also <see cref="char.IsWhiteSpace(char)"/>.</summary>
    internal static IReadOnlySet<char> NetSpace { get; }

    /// <summary>The zero of each run of ten decimal digits .NET's Regex reads as <c>\d</c>.</summary>
    internal static IReadOnlyList<int> DigitZeros { get; }

    /// <summary>The classes the shared patterns' <c>%name%</c> placeholders stand for, as <c>checks.CLASSES</c> builds them.</summary>
    internal static IReadOnlyDictionary<string, string> Classes { get; }

#pragma warning disable CA1810 // One read of one file fills every table; field initialisers would read it six times.
    static DraftReading()
#pragma warning restore CA1810
    {
        using var stream = typeof(DraftReading).Assembly.GetManifestResourceStream("ScreenTail.Api.Summarize.unicode-tables.json")
            ?? throw new InvalidOperationException("unicode-tables.json is not embedded in the API assembly.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        foreach (var entry in root.GetProperty("decompositions").EnumerateObject())
        {
            Decompositions[Hex(entry.Name)] = [.. entry.Value.GetString()!.Split(' ').Select(Hex)];
        }

        foreach (var entry in root.GetProperty("combining_classes").EnumerateObject())
        {
            var cls = int.Parse(entry.Name, CultureInfo.InvariantCulture);
            foreach (var cp in CodePoints(entry.Value.GetString()!))
            {
                Combining[cp] = cls;
            }
        }

        foreach (var line in root.GetProperty("compositions").EnumerateArray())
        {
            var parts = line.GetString()!.Split(' ');
            Compositions[Pair(Hex(parts[0]), Hex(parts[1]))] = Hex(parts[2]);
        }

        Unseen.UnionWith(CodePoints(root.GetProperty("unseen").GetString()!));
        Spaces.UnionWith(CodePoints(root.GetProperty("spaces").GetString()!));
        UnicodeVersion = root.GetProperty("unicode_version").GetString()!;
        Assigned = CodePoints(root.GetProperty("assigned").GetString()!).ToHashSet();

        var netSpace = root.GetProperty("net_space").GetString()!;
        NetSpace = CodePoints(netSpace).Select(cp => (char)cp).ToHashSet();
        DigitZeros = [.. root.GetProperty("digit_zeros").GetString()!.Split(' ').Select(Hex)];
        Classes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["word"] = CharClass(root.GetProperty("word").GetString()!, negated: false),
            ["digit"] = "[" + string.Concat(DigitZeros.Select(z => $"\\u{z:X4}-\\u{z + 9:X4}")) + "]",
            ["space"] = CharClass(netSpace, negated: false),
            ["nonspace"] = CharClass(netSpace, negated: true),
        };
    }

    /// <summary>The text as a person reads it on screen. The steps are in the summary of this class.</summary>
    public static string Read(string text)
    {
        var read = new StringBuilder(text?.Length ?? 0);
        var lastWasSpace = false;
        foreach (var point in NfkcPoints(text))
        {
            var cp = point;
            if (Unseen.Contains(cp))
            {
                continue;
            }

            if (Spaces.Contains(cp))
            {
                cp = ' ';
            }

            if (cp == ' ' && lastWasSpace)
            {
                continue;
            }

            lastWasSpace = cp == ' ';
            _ = cp > 0xFFFF ? read.Append(char.ConvertFromUtf32(cp)) : read.Append((char)cp);
        }

        return read.ToString();
    }

    /// <summary>The first step on its own: lone surrogates made U+FFFD, then NFKC from the shared tables.</summary>
    internal static string Nfkc(string text) => string.Concat(NfkcPoints(text).Select(char.ConvertFromUtf32));

    private static List<int> NfkcPoints(string? text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var points = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                cp = 0xFFFD;
            }

            Decompose(cp, points);
        }

        Order(points);
        return Compose(points);
    }

    /// <summary>
    /// The text as the model wrote it, with one change: U+212A KELVIN SIGN is read as K, because .NET's
    /// IgnoreCase takes it for k and Python's ASCII folding does not, and a rule that ignores case has to
    /// mean the same in both.
    /// </summary>
    public static string Written(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace('\u212A', 'K');
    }

    private static void Decompose(int cp, List<int> into)
    {
        if (cp is >= SBase and < SBase + SCount)
        {
            var index = cp - SBase;
            into.Add(LBase + (index / NCount));
            into.Add(VBase + (index % NCount / TCount));
            if (index % TCount != 0)
            {
                into.Add(TBase + (index % TCount));
            }
        }
        else if (Decompositions.TryGetValue(cp, out var parts))
        {
            into.AddRange(parts);
        }
        else
        {
            into.Add(cp);
        }
    }

    private static int ClassOf(int cp) => Combining.TryGetValue(cp, out var cls) ? cls : 0;

    /// <summary>Canonical ordering: each run of combining marks, sorted stably by combining class.</summary>
    private static void Order(List<int> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var cp = points[i];
            var cls = ClassOf(cp);
            if (cls == 0)
            {
                continue;
            }

            var j = i;
            while (j > 0 && ClassOf(points[j - 1]) > cls)
            {
                points[j] = points[j - 1];
                j--;
            }

            points[j] = cp;
        }
    }

    /// <summary>Canonical composition, the textbook algorithm of UAX #15.</summary>
    private static List<int> Compose(List<int> points)
    {
        var result = new List<int>(points.Count);
        if (points.Count == 0)
        {
            return result;
        }

        result.Add(points[0]);
        var starter = 0;
        var last = ClassOf(points[0]) == 0 ? 0 : 256;
        for (var i = 1; i < points.Count; i++)
        {
            var cp = points[i];
            var cls = ClassOf(cp);
            if (Composite(result[starter], cp) is { } composite && (last < cls || last == 0))
            {
                result[starter] = composite;
                continue;
            }

            if (cls == 0)
            {
                starter = result.Count;
            }

            last = cls;
            result.Add(cp);
        }

        return result;
    }

    private static int? Composite(int first, int second)
    {
        if (first is >= LBase and < LBase + LCount && second is >= VBase and < VBase + VCount)
        {
            return SBase + ((((first - LBase) * VCount) + (second - VBase)) * TCount);
        }

        if (first is >= SBase and < SBase + SCount && (first - SBase) % TCount == 0 && second is > TBase and < TBase + TCount)
        {
            return first + (second - TBase);
        }

        return Compositions.TryGetValue(Pair(first, second), out var composite) ? composite : null;
    }

    private static long Pair(int first, int second) => ((long)first << 21) | (uint)second;

    private static int Hex(string value) => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static IEnumerable<int> CodePoints(string spec)
    {
        foreach (var part in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            var start = Hex(dash < 0 ? part : part[..dash]);
            var end = dash < 0 ? start : Hex(part[(dash + 1)..]);
            for (var cp = start; cp <= end; cp++)
            {
                yield return cp;
            }
        }
    }

    private static string CharClass(string spec, bool negated)
    {
        var body = new StringBuilder("[");
        if (negated)
        {
            _ = body.Append('^');
        }

        foreach (var part in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            _ = dash < 0
                ? body.Append("\\u").Append(part)
                : body.Append("\\u").Append(part[..dash]).Append("-\\u").Append(part[(dash + 1)..]);
        }

        return body.Append(']').ToString();
    }
}
