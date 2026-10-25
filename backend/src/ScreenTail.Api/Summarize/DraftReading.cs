using System.Buffers;
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
///
/// <b>Bounded, and refused past the bound.</b> The validator reads the transcript a device sent as well as
/// the draft, and BundleLimits lets a bundle carry 600,000 characters of it. NFKC makes U+FDFA eighteen
/// characters, so a bundle of it read as 10.8 million code points, held three times over (PR #206 review).
/// A text is read only while its decomposition, which is never shorter than its NFKC, stays within
/// <see cref="Growth"/> code points for each of the text's own and <see cref="Slack"/> more. Past that,
/// <see cref="Read"/> stops counting and answers null, and the validator refuses the text: it cannot be
/// checked, so it may not be shown. Truncating instead would leave the rest of the text unread by every
/// rule that reads it as read, which is the hole the reading exists to close.
///
/// Four is as far as ordinary text grows: Hangul at its worst is three code points a
/// syllable, polytonic Greek four, and the compatibility characters people type (㎒, ﬃ, ½, ⑽) three or
/// four. The slack lets a short field hold a few of the longer ones. Only text made mostly of the ligatures
/// that grow further (U+FDFA, U+FDFB, ㎯) passes it. The read is then linear in the text: one pass counts,
/// one fills a buffer of exactly the counted size from the shared pool, canonical ordering sorts each run
/// of marks by counting rather than by insertion (a run of thousands was quadratic), and composition and
/// the rest work in place. A plain ASCII text with no runs of spaces is its own reading, and costs
/// nothing.
/// </summary>
internal static class DraftReading
{
    private const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    private const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    /// <summary>Code points kept in a buffer on the stack rather than one from the pool.</summary>
    private const int OnTheStack = 256;

    /// <summary>The longest run of marks sorted by insertion. Past it, by counting, which is linear.</summary>
    private const int InsertionRun = 32;

    private const byte Kept = 0, Unseen = 1, Space = 2;

    /// <summary>What <see cref="BmpTables"/> says of a BMP code point, so the common case asks no dictionary.</summary>
    private const byte Decomposes = 1, ComposesFirst = 2;

    /// <summary>Code points a text's decomposition may hold for each of its own (<c>read_growth</c>).</summary>
    internal static readonly int Growth = int.Parse(DraftRules.ReadGrowth, CultureInfo.InvariantCulture);

    /// <summary>Code points a text's decomposition may hold on top of that (<c>read_slack</c>).</summary>
    internal static readonly int Slack = int.Parse(DraftRules.ReadSlack, CultureInfo.InvariantCulture);

    private static readonly Dictionary<int, int[]> Decompositions = [];
    private static readonly Dictionary<long, int> Compositions = [];

    /// <summary>Each BMP code point's combining class, and the few outside the BMP that have one.</summary>
    private static readonly byte[] BmpClass = new byte[0x10000];
    private static readonly Dictionary<int, byte> AstralClass = [];

    /// <summary>Whether each code point is kept, removed (format characters) or made a space.</summary>
    private static readonly byte[] BmpKind = new byte[0x10000];
    private static readonly Dictionary<int, byte> AstralKind = [];

    /// <summary>Whether each BMP code point has a decomposition, and whether it is the first of a composition.</summary>
    private static readonly byte[] BmpTables = new byte[0x10000];

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
            var cp = Hex(entry.Name);
            Decompositions[cp] = [.. entry.Value.GetString()!.Split(' ').Select(Hex)];
            if (cp < 0x10000)
            {
                BmpTables[cp] |= Decomposes;
            }
        }

        foreach (var entry in root.GetProperty("combining_classes").EnumerateObject())
        {
            var cls = byte.Parse(entry.Name, CultureInfo.InvariantCulture);
            foreach (var cp in CodePoints(entry.Value.GetString()!))
            {
                Set(BmpClass, AstralClass, cp, cls);
            }
        }

        foreach (var line in root.GetProperty("compositions").EnumerateArray())
        {
            var parts = line.GetString()!.Split(' ');
            var first = Hex(parts[0]);
            Compositions[Pair(first, Hex(parts[1]))] = Hex(parts[2]);
            if (first < 0x10000)
            {
                BmpTables[first] |= ComposesFirst;
            }
        }

        foreach (var cp in CodePoints(root.GetProperty("unseen").GetString()!))
        {
            Set(BmpKind, AstralKind, cp, Unseen);
        }

        foreach (var cp in CodePoints(root.GetProperty("spaces").GetString()!))
        {
            Set(BmpKind, AstralKind, cp, Space);
        }

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

    /// <summary>
    /// The text as a person reads it on screen, or null when it grows past the bound and so cannot be read.
    /// The steps are in the summary of this class.
    /// </summary>
    public static string? Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsPlain(text))
        {
            return text;
        }

        var length = DecomposedLength(text);
        if (length < 0)
        {
            return null;
        }

        int[]? rented = null;
        Span<int> points = length <= OnTheStack ? stackalloc int[OnTheStack] : (rented = ArrayPool<int>.Shared.Rent(length));
        try
        {
            points = points[..length];
            Decompose(text, points);
            Order(points);
            return Shown(points[..Compose(points)]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>The first step on its own: lone surrogates made U+FFFD, then NFKC from the shared tables. Null past the bound.</summary>
    internal static string? Nfkc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = DecomposedLength(text);
        if (length < 0)
        {
            return null;
        }

        var points = new int[length];
        Decompose(text, points);
        Order(points);
        var count = Compose(points);
        var chars = new char[count * 2];
        var n = 0;
        foreach (var cp in points.AsSpan(0, count))
        {
            n += new Rune(cp).EncodeToUtf16(chars.AsSpan(n));
        }

        return new string(chars, 0, n);
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

    /// <summary>
    /// Whether the text reads as itself: ASCII, which NFKC leaves alone and has no format characters, with
    /// no space but a plain one and never two of those together.
    /// </summary>
    private static bool IsPlain(string text)
    {
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (c >= 0x80 || (c != ' ' && BmpKind[c] != Kept) || (c == ' ' && lastWasSpace))
            {
                return false;
            }

            lastWasSpace = c == ' ';
        }

        return true;
    }

    /// <summary>The next scalar value, as .NET decodes UTF-16: a pair is one, and a lone surrogate U+FFFD.</summary>
    private static int NextScalar(string text, ref int i)
    {
        var c = text[i++];
        if (char.IsHighSurrogate(c) && i < text.Length && char.IsLowSurrogate(text[i]))
        {
            return char.ConvertToUtf32(c, text[i++]);
        }

        return char.IsSurrogate(c) ? 0xFFFD : c;
    }

    /// <summary>
    /// How many code points the text decomposes to, or -1 once that passes the bound. Nothing is allocated
    /// to find out, so a text that is refused costs one pass over as much of it as it took to tell.
    /// </summary>
    private static int DecomposedLength(string text)
    {
        var scalars = 0L;
        for (var i = 0; i < text.Length; scalars++)
        {
            _ = NextScalar(text, ref i);
        }

        var limit = Math.Min((Growth * scalars) + Slack, Array.MaxLength);
        var length = 0L;
        for (var i = 0; i < text.Length;)
        {
            var cp = NextScalar(text, ref i);
            length += cp is >= SBase and < SBase + SCount
                ? ((cp - SBase) % TCount == 0 ? 2 : 3)
                : MayDecompose(cp) && Decompositions.TryGetValue(cp, out var parts) ? parts.Length : 1;
            if (length > limit)
            {
                return -1;
            }
        }

        return (int)length;
    }

    /// <summary>The text's full compatibility decomposition, into a buffer <see cref="DecomposedLength"/> sized.</summary>
    private static void Decompose(string text, Span<int> into)
    {
        var n = 0;
        for (var i = 0; i < text.Length;)
        {
            var cp = NextScalar(text, ref i);
            if (cp is >= SBase and < SBase + SCount)
            {
                var index = cp - SBase;
                into[n++] = LBase + (index / NCount);
                into[n++] = VBase + (index % NCount / TCount);
                if (index % TCount != 0)
                {
                    into[n++] = TBase + (index % TCount);
                }
            }
            else if (MayDecompose(cp) && Decompositions.TryGetValue(cp, out var parts))
            {
                parts.CopyTo(into[n..]);
                n += parts.Length;
            }
            else
            {
                into[n++] = cp;
            }
        }
    }

    private static bool MayDecompose(int cp) => cp >= 0x10000 || (BmpTables[cp] & Decomposes) != 0;

    private static int ClassOf(int cp) => cp < 0x10000 ? BmpClass[cp] : AstralClass.GetValueOrDefault(cp);

    private static byte KindOf(int cp) => cp < 0x10000 ? BmpKind[cp] : AstralKind.GetValueOrDefault(cp);

    /// <summary>
    /// Canonical ordering: each run of combining marks, sorted stably by combining class. Swapping
    /// neighbours, as UAX #15 describes it, is this sort, and insertion is that sort done one swap at a
    /// time, which over a run of thousands of marks is millions.
    /// </summary>
    private static void Order(Span<int> points)
    {
        for (var i = 0; i < points.Length;)
        {
            if (ClassOf(points[i]) == 0)
            {
                i++;
                continue;
            }

            var end = i + 1;
            while (end < points.Length && ClassOf(points[end]) != 0)
            {
                end++;
            }

            if (end - i > 1)
            {
                Sort(points[i..end]);
            }

            i = end;
        }
    }

    /// <summary>One run of marks, sorted stably by class: by insertion when short, by counting when not.</summary>
    private static void Sort(Span<int> run)
    {
        if (run.Length <= InsertionRun)
        {
            for (var i = 1; i < run.Length; i++)
            {
                var cp = run[i];
                var cls = ClassOf(cp);
                var j = i;
                while (j > 0 && ClassOf(run[j - 1]) > cls)
                {
                    run[j] = run[j - 1];
                    j--;
                }

                run[j] = cp;
            }

            return;
        }

        Span<int> starts = stackalloc int[256];
        starts.Clear();
        foreach (var cp in run)
        {
            starts[ClassOf(cp)]++;
        }

        var total = 0;
        for (var cls = 0; cls < starts.Length; cls++)
        {
            (starts[cls], total) = (total, total + starts[cls]);
        }

        var rented = ArrayPool<int>.Shared.Rent(run.Length);
        try
        {
            var sorted = rented.AsSpan(0, run.Length);
            foreach (var cp in run)
            {
                sorted[starts[ClassOf(cp)]++] = cp;
            }

            sorted.CopyTo(run);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>Canonical composition, the textbook algorithm of UAX #15, in place. Returns how many are left.</summary>
    private static int Compose(Span<int> points)
    {
        if (points.Length == 0)
        {
            return 0;
        }

        var kept = 1;
        var starter = 0;
        var last = ClassOf(points[0]) == 0 ? 0 : 256;
        for (var i = 1; i < points.Length; i++)
        {
            var cp = points[i];
            var cls = ClassOf(cp);
            if ((last < cls || last == 0) && Composite(points[starter], cp) is { } composite)
            {
                points[starter] = composite;
                continue;
            }

            if (cls == 0)
            {
                starter = kept;
            }

            last = cls;
            points[kept++] = cp;
        }

        return kept;
    }

    /// <summary>The composed text with format characters removed and every run of spaces made one.</summary>
    private static string Shown(Span<int> points)
    {
        var kept = 0;
        var units = 0;
        var lastWasSpace = false;
        foreach (var point in points)
        {
            var cp = point;
            var kind = KindOf(cp);
            if (kind == Unseen)
            {
                continue;
            }

            if (kind == Space)
            {
                cp = ' ';
            }

            if (cp == ' ' && lastWasSpace)
            {
                continue;
            }

            lastWasSpace = cp == ' ';
            points[kept++] = cp;
            units += cp > 0xFFFF ? 2 : 1;
        }

        char[]? rented = null;
        Span<char> chars = units <= OnTheStack ? stackalloc char[OnTheStack] : (rented = ArrayPool<char>.Shared.Rent(units));
        try
        {
            var n = 0;
            foreach (var cp in points[..kept])
            {
                n += new Rune(cp).EncodeToUtf16(chars[n..]);
            }

            return new string(chars[..n]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
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

        if (first < 0x10000 && (BmpTables[first] & ComposesFirst) == 0)
        {
            return null;
        }

        return Compositions.TryGetValue(Pair(first, second), out var composite) ? composite : null;
    }

    private static void Set(byte[] bmp, Dictionary<int, byte> astral, int cp, byte value)
    {
        if (cp < 0x10000)
        {
            bmp[cp] = value;
        }
        else
        {
            astral[cp] = value;
        }
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
