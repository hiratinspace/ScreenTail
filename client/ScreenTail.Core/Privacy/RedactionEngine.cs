using System.Text;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <param name="Text">OCR text for one word, with the box it came from (ST-041 supplies these).</param>
public sealed record OcrWord(string Text, int X, int Y, int Width, int Height);

/// <param name="Text">The text with every match replaced by its label.</param>
/// <param name="Counts">How many of each kind were found — the only thing that may be logged (INV-10).</param>
/// <param name="Complete">
/// False when a detector ran out of its match budget, which means this text was never fully searched.
/// A false here is not "nothing found": the caller must discard the frame or segment rather than store it.
/// </param>
public sealed record ScrubResult(string Text, IReadOnlyDictionary<MaskKind, int> Counts, bool Complete = true)
{
    public bool Changed => Counts.Count > 0;

    public int Total => Counts.Values.Sum();
}

/// <param name="Regions">Boxes to paint over in the stored frame, before it is readable by anything else (INV-1).</param>
/// <param name="Complete">False when the scan did not finish; the frame must be purged, not stored.</param>
public sealed record FrameRedaction(
    string Text,
    IReadOnlyList<MaskedRegion> Regions,
    IReadOnlyDictionary<MaskKind, int> Counts,
    bool Complete = true);

/// <summary>
/// Pattern-based redaction (ST-042). Two entry points: <see cref="ScrubText"/> for transcript segments and any
/// text on its way to a draft, and <see cref="RedactFrame"/> for an OCR result, which additionally reports the
/// boxes to mask in the image. The engine is pure: the caller writes the masked image and the scrubbed text.
/// </summary>
public sealed class RedactionEngine
{
    private RedactionPolicy _policy;
    private readonly Func<string, RedactionPolicy, Action, IEnumerable<PatternMatch>> _find;

    public RedactionEngine(RedactionPolicy? policy = null)
        : this(policy, PatternLibrary.Find)
    {
    }

    /// <summary>Test seam: lets a test stand in for the pattern library, e.g. to simulate a detector timing out.</summary>
    internal RedactionEngine(RedactionPolicy? policy, Func<string, RedactionPolicy, Action, IEnumerable<PatternMatch>> find)
    {
        _policy = policy ?? RedactionPolicy.Default;
        _find = find;
    }

    /// <summary>
    /// Test seam: false leaves the wrap pass out of <see cref="RedactFrame"/>, so a test can hold the
    /// engine with it to masking everything the engine without it masks. Nothing else sets it.
    /// </summary>
    internal bool Wraps { get; init; } = true;

    /// <summary>The technician's settings arriving (ST-081): every frame and segment after this one is scrubbed under the new policy.</summary>
    public void Apply(RedactionPolicy policy) => _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public ScrubResult ScrubText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (matches, complete) = Resolve(text);
        return new ScrubResult(Replace(text, matches), Count(matches), complete);
    }

    /// <summary>
    /// Writes the replacements into the text. Separated from <see cref="ScrubText"/> so that a caller
    /// which has already resolved the matches — <see cref="RedactFrame"/>, which needs them to place the
    /// mask boxes — can produce the stored string without searching the same page a second time
    /// (ST-048, weaknesses P2-1).
    /// </summary>
    private static string Replace(string text, List<PatternMatch> matches)
    {
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var match in matches)
        {
            builder.Append(text, cursor, match.Start - cursor).Append(match.Replacement);
            cursor = match.End;
        }

        return builder.Append(text, cursor, text.Length - cursor).ToString();
    }

    /// <summary>
    /// Runs the patterns over an OCR result. The returned text is what may be stored; the regions are the
    /// boxes the image masker must paint before the frame can be read by Review, drafting or export.
    /// </summary>
    public FrameRedaction RedactFrame(IReadOnlyList<OcrWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        // Rebuild the page text and remember where each word sits in it, so a match maps back to boxes.
        var builder = new StringBuilder();
        var starts = new int[words.Count];
        var ends = new int[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            starts[i] = builder.Length;
            builder.Append(words[i].Text);
            ends[i] = builder.Length;
        }

        var text = builder.ToString();
        var (matches, complete) = Resolve(text);

        var page = new OcrPage(words);
        var regions = new List<MaskedRegion>();
        var widened = new List<PatternMatch>(matches.Count);
        var counts = Count(matches);
        var taken = new HashSet<int>();
        foreach (var match in matches)
        {
            // Every word the match touches is masked whole: a partly painted number is still readable.
            if (Touching(starts, ends, match) is not var (first, last))
            {
                continue;
            }

            var cues = new List<int>(match.CueStarts.Count);
            foreach (var start in match.CueStarts)
            {
                if (WordAt(starts, ends, start) is >= 0 and var cue)
                {
                    cues.Add(cue);
                }
            }

            _ = Mask(page, starts, ends, [.. Enumerable.Range(first, last - first + 1)], cues, match, taken, regions, widened);
        }

        // And the page read again with every line that may have wrapped joined to the one that may
        // continue it (OcrWraps): a key cut in two by its window's edge is two words above, and no rule
        // matches half a key. Only a match that crosses a join is taken from this reading, so it adds
        // masks and takes none away. A page of one row has nothing wrapped on it.
        if (Wraps && page.RowCount > 1)
        {
            var wraps = new OcrWraps(page);
            if (wraps.Any)
            {
                var (wrapped, wrappedComplete) = Resolve(wraps.Text);
                complete &= wrappedComplete;
                foreach (var match in wrapped)
                {
                    // Whatever crosses a join, however many: a card number over four rows is one
                    // match across three. A column of short numbers that joined pass the checksum
                    // is masked with it, since nothing on the screen tells the two apart.
                    if (!wraps.Crosses(match.Start, match.End))
                    {
                        continue;
                    }

                    var cues = new List<int>(match.CueStarts.Count);
                    foreach (var start in match.CueStarts)
                    {
                        if (wraps.WordAt(start) is >= 0 and var cue)
                        {
                            cues.Add(cue);
                        }
                    }

                    // Counted only where it is a find of its own: one that reaches a word an earlier
                    // match masked is that match, seen whole this time.
                    if (Mask(page, starts, ends, [.. wraps.Touching(match.Start, match.End)], cues, match, taken, regions, widened))
                    {
                        counts[match.Kind] = counts.GetValueOrDefault(match.Kind) + 1;
                    }
                }
            }
        }

        if (widened.Count == 0)
        {
            return new FrameRedaction(text, [], counts, complete);
        }

        // A match's runs can sit either side of another match's in the text; Merge and Replace walk it
        // once, forward.
        widened.Sort((a, b) => a.Start.CompareTo(b.Start));

        // The same matches that placed the boxes write the stored text. Resolving twice gave two answers
        // that were identical by construction and cost the pattern library a second full pass over every
        // frame — on the path ADR-0001 measured worst at 2012 ms against a 700 ms budget.
        return new FrameRedaction(Replace(text, Merge(widened)), regions, counts, complete);
    }

    /// <summary>
    /// Masks what one match takes on the screen: the words it touches, their tokens, and for a password
    /// its rows. Adds the boxes to paint and the spans the stored text loses.
    /// </summary>
    /// <returns>Whether none of it had been masked by a match before this one.</returns>
    private static bool Mask(
        OcrPage page,
        int[] starts,
        int[] ends,
        List<int> touched,
        List<int> cues,
        PatternMatch match,
        HashSet<int> taken,
        List<MaskedRegion> regions,
        List<PatternMatch> widened)
    {
        // Every token the match touches. The engine can read one token on screen as several
        // words: "pass: Tangerine4821Wobble" came back as "Tangerine4821" and "Wobble", and the
        // password rule, which ends a value at the first plain word, masked the first and left the
        // second in the stored text and unpainted in the frame (loop-13, 2026-11-08). The words of
        // one token have no space between them on the screen, whatever the text says, so the token
        // is found on the screen: a piece Windows listed in an OcrLine of its own, or to the left of
        // the match, is as much a part of it as the next word in the list.
        var masked = Token(page, touched);

        // A password takes its rows as well (owner decision 2026-11-09, #268); see PasswordRows.
        // Keyed on the cues the match carries, not its kind, because a password another rule also
        // claimed is merged under that rule's kind.
        if (match.CueStarts.Count > 0)
        {
            PasswordRows(page, touched, cues, masked);
        }

        if (masked.IsSubsetOf(taken))
        {
            return false;
        }

        var fresh = !masked.Overlaps(taken);
        taken.UnionWith(masked);

        // One box per run of masked words on a row, so the picture loses what the text loses and no
        // more: a box around the whole match painted over words the text kept.
        regions.AddRange(Paint(page, masked, match.Kind));

        // And the stored text loses the same words the picture did.
        //
        // It used to lose only the matched span, so a value the pattern stopped short of kept its
        // tail: "P@ss.word1!" is one OCR word, the password rule's value ends at the full stop, and
        // ".word1!" stayed in ocr_text — painted out of the frame and sent to the model in writing
        // (2026-09-19 review). Picture and text are two views of one decision and must agree. A
        // token listed in two places in the text is replaced in both.
        var ordered = masked.Order().ToArray();
        var run = 0;
        for (var i = 1; i <= ordered.Length; i++)
        {
            if (i < ordered.Length && ordered[i] == ordered[i - 1] + 1)
            {
                continue;
            }

            widened.Add(match with { Start = starts[ordered[run]], Length = ends[ordered[i - 1]] - starts[ordered[run]] });
            run = i;
        }

        return fresh;
    }

    /// <summary>The first and last word a match touches in the text, by binary search, or null for none.</summary>
    private static (int First, int Last)? Touching(int[] starts, int[] ends, PatternMatch match)
    {
        // The first word ending after the match starts, and the last starting before it ends.
        var first = LowerBound(ends, match.Start + 1);
        var last = LowerBound(starts, match.End) - 1;
        return first <= last ? (first, last) : null;
    }

    /// <summary>The word whose text holds the character at <paramref name="offset"/>, or -1.</summary>
    private static int WordAt(int[] starts, int[] ends, int offset)
    {
        var word = LowerBound(starts, offset + 1) - 1;
        return word >= 0 && offset < ends[word] ? word : -1;
    }

    /// <summary>The first index whose value is at least <paramref name="value"/>, in an ascending array.</summary>
    private static int LowerBound(int[] sorted, int value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Joins matches that widening has made touch or overlap.
    ///
    /// <see cref="Replace"/> walks a cursor forward and would write the text between two overlapping
    /// replacements backwards, or throw. Two secrets in adjacent words become one marker, which reads
    /// no worse and hides no less.
    /// </summary>
    private static List<PatternMatch> Merge(List<PatternMatch> matches)
    {
        var merged = new List<PatternMatch>(matches.Count);
        foreach (var match in matches)
        {
            if (merged.Count > 0 && match.Start <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = previous with { Length = Math.Max(previous.End, match.End) - previous.Start };
                continue;
            }

            merged.Add(match);
        }

        return merged;
    }

    /// <summary>
    /// Sorted, non-overlapping matches: earliest first, and the longest wins a tie.
    ///
    /// <b>Overlaps are merged, not dropped.</b> This kept a running end and skipped any match beginning
    /// before it — the whole match, including the part that reached past. Where two rules overlap, the
    /// tail of the second stayed in the text with nothing over it: the card and SSN detectors can both
    /// claim parts of one run of digits, and on a transcript there is no widening to whole words
    /// afterwards to cover the gap (2026-09-20 review).
    ///
    /// The union takes the first match's kind and marker. Two rules disagreeing about what a span is
    /// matters far less than a span nobody masked, and the alternative — a second marker butted against
    /// the first — would label a fragment rather than a thing.
    /// </summary>
    private (List<PatternMatch> Matches, bool Complete) Resolve(string text)
    {
        var complete = true;
        var found = _find(text, _policy, () => complete = false)
            .OrderBy(m => m.Start)
            .ThenByDescending(m => m.Length)
            .ToList();

        var resolved = new List<PatternMatch>(found.Count);
        foreach (var match in found)
        {
            if (resolved.Count == 0 || match.Start >= resolved[^1].End)
            {
                resolved.Add(match);
                continue;
            }

            // Overlapping. Grow the one already kept to cover both, unless it already does, and keep a
            // password cue either brought: a password another rule also claimed still takes its row.
            var previous = resolved[^1];
            resolved[^1] = previous with
            {
                Length = Math.Max(previous.End, match.End) - previous.Start,
                CueStarts = [.. previous.CueStarts, .. match.CueStarts],
            };
        }

        return (resolved, complete);
    }

    /// <summary>
    /// The widest gap, as a share of the row's height, that two OCR words can have between their boxes
    /// and still be one token on the screen. See <see cref="GapShare"/> for what the height is.
    ///
    /// Measured on Windows OCR (ScreenTail.Tests.Windows OcrSplitSecretTests, hosted runner, 2026-11-08,
    /// 1,000 renderings and 15,000 real spaces at each of 24, 14, 11 and 9 pt): inside a token the engine
    /// split, 0.03 to 0.29 at 24, 14 and 11 pt and up to 0.33 at 9 pt, never closer to this line than
    /// 0.9 px above 9 pt and 0.2 px at 9 pt. At a real space, 0.28 to 0.56, so the two overlap and no
    /// line parts them. It sits above every split on the side that masks more: a token left half masked
    /// is a secret stored, a space taken for none is one more word under the marker. The cost, at that
    /// probe's sizes: about one space in six joined at 24 pt, one in four to three at 14 and 11 pt, and
    /// two in three at 9 pt, where a space is three pixels and the engine rounds boxes to the pixel.
    /// </summary>
    internal const double TokenGap = 0.35;

    /// <summary>
    /// The words of the token, or tokens, that the words in <paramref name="seeds"/> are part of: every word reached from them through a neighbour on the same row closer than
    /// <see cref="TokenGap"/>, whichever side it is on and wherever the engine listed it.
    /// </summary>
    private static HashSet<int> Token(OcrPage page, List<int> seeds)
    {
        var words = page.Words;
        var token = new HashSet<int>();
        var waiting = new Queue<int>();
        foreach (var i in seeds)
        {
            if (token.Add(i))
            {
                waiting.Enqueue(i);
            }
        }

        while (waiting.TryDequeue(out var i))
        {
            var row = page.RowAt(page.RowOf(i));

            // No neighbour sits further away than the cutoff at the tallest height a pair is measured
            // against (TokenHeight), and only one that shares its row (Neighbours): the row's height,
            // a word's up to twice that, and for two words on one line either's own, however tall.
            // This word's own height is in the reach; a taller word that reaches this one is found
            // from its side (TallNear), so no word looks further than its own height says.
            var reach = TokenGap * Math.Max(Math.Max(row.Height, Math.Min(row.MaxHeight, 2.0 * row.Height)), words[i].Height);
            foreach (var j in page.Near(i, reach).Concat(page.TallNear(i)))
            {
                if (!token.Contains(j) && Neighbours(words[i], words[j], row.Height) && GapShare(words[i], words[j], row.Height) <= TokenGap)
                {
                    _ = token.Add(j);
                    waiting.Enqueue(j);
                }
            }
        }

        return token;
    }

    /// <summary>
    /// A password's rows (owner decision 2026-11-09, #268): the cue's row and every row the match
    /// touches, found on the screen, all masked, the cue included.
    ///
    /// It infers nothing. Reading which way a row runs, or which word was the secret, from the engine's
    /// list order left a password's row readable in four ways the security reviews found, because list
    /// order is not reading order. ScreenTail's cues are Latin, so the one exception is on the cue's own
    /// row: a word wholly left of the cue stays, so the line still says what it was, unless the match or
    /// its tokens took anything on that row that starts before the cue ends. A row with any right-to-left
    /// letter on it may run the other way, and is masked whole.
    ///
    /// Why at all: the token rule was measured on one font at 96 DPI, while the frames that matter come
    /// through a remote viewer's scaling and compression (INV-5), where a split can sit wider than any
    /// measured, and the token rule already masks the next word in 16 to 29% of cases, so the row costs
    /// little more to read.
    /// </summary>
    private static void PasswordRows(
        OcrPage page,
        List<int> touched,
        List<int> cueWords,
        HashSet<int> masked)
    {
        var words = page.Words;
        var rows = new HashSet<int>();
        foreach (var i in touched)
        {
            _ = rows.Add(page.RowOf(i));
        }

        // Per cue row, its leftmost cue: only what ends at or left of it is kept.
        var cues = new HashSet<int>();
        var leftmost = new Dictionary<int, int>();
        foreach (var cue in cueWords)
        {
            _ = cues.Add(cue);
            var row = page.RowOf(cue);
            _ = rows.Add(row);
            if (!leftmost.TryGetValue(row, out var other) || words[cue].X < words[other].X)
            {
                leftmost[row] = cue;
            }
        }

        // And only when nothing the match or its tokens took on that row starts before the cue ends: a
        // secret the engine placed left of its cue says the row does not read the way the cue does, and
        // what is left of the cue is then no safer than what is right of it (security review of #268).
        var keepLeftOf = new Dictionary<int, int>();
        foreach (var (row, cue) in leftmost)
        {
            var cueRight = words[cue].X + words[cue].Width;
            if (masked.All(w => cues.Contains(w) || page.RowOf(w) != row || words[w].X >= cueRight))
            {
                keepLeftOf[row] = words[cue].X;
            }
        }

        foreach (var row in rows)
        {
            var members = page.RowAt(row);
            int? keep = !members.RightToLeft && keepLeftOf.TryGetValue(row, out var left) ? left : null;
            foreach (var word in members.Members)
            {
                if (keep is { } edge && !cues.Contains(word) && words[word].X + words[word].Width <= edge)
                {
                    continue;
                }

                _ = masked.Add(word);
            }
        }
    }

    /// <summary>Whether two words share a row: the middle of either lies within the other's height.</summary>
    /// <remarks>
    /// Either, because a lowercase word's box is only as tall as the x-height and sits inside its
    /// neighbours'.
    /// </remarks>
    internal static bool SameRow(OcrWord a, OcrWord b)
    {
        var middleA = a.Y + (a.Height / 2.0);
        var middleB = b.Y + (b.Height / 2.0);
        return (middleB >= a.Y && middleB <= a.Y + a.Height) || (middleA >= b.Y && middleA <= b.Y + b.Height);
    }

    /// <summary>
    /// Whether the token search may step between two words of one row: they share a row
    /// (<see cref="SameRow"/>), and where either is taller than <see cref="TokenHeight"/> lets a word
    /// count, each one's middle lies within the other's height.
    /// </summary>
    /// <remarks>
    /// A row can take in several lines where one box spans them, and a box as tall as the page shares a
    /// row with every word on it: a token that stepped through it flooded the page (review of #268 at
    /// a3cd033: one email took 7,999 words, and 100 cues 30 s). Asking both ways of every pair stopped
    /// that and dropped a baseline underscore, a full stop or a zero-height box from the middle of a key,
    /// with the key's tail after it (security review, same day). So only a box too tall to be a piece of
    /// text is held to both ways.
    /// </remarks>
    internal static bool Neighbours(OcrWord a, OcrWord b, double rowHeight)
    {
        if (!SameRow(a, b))
        {
            return false;
        }

        if (Math.Max(a.Height, b.Height) <= 2.0 * rowHeight)
        {
            return true;
        }

        var middleA = a.Y + (a.Height / 2.0);
        var middleB = b.Y + (b.Height / 2.0);
        return middleB >= a.Y && middleB <= a.Y + a.Height && middleA >= b.Y && middleA <= b.Y + b.Height;
    }

    /// <summary>
    /// The space between two words' boxes on a row as a share of <see cref="TokenHeight"/>. The space
    /// is the horizontal distance in either direction.
    /// </summary>
    internal static double GapShare(OcrWord a, OcrWord b, double rowHeight)
    {
        var gap = Math.Max(b.X - (a.X + a.Width), a.X - (b.X + b.Width));
        return gap / TokenHeight(a, b, rowHeight);
    }

    /// <summary>
    /// The height a space between two words is measured against: the largest of the two words' and
    /// their row's (<see cref="OcrPage.Row.Height"/>). A piece with no capital, ascender or digit is
    /// only x-height tall, about 0.7 of its neighbours, and measured against itself a split looked a
    /// third wider than it is (review of #268); each of those only makes the share smaller, so each
    /// only masks more. A word counts at most twice the row's height, so a box as tall as the page
    /// does not reach across it, and the height is never under a pixel.
    ///
    /// Unless the two are on one line by either's measure, each one's middle within the other's
    /// height: then their own heights count whole. A row's height is its median, and a few small boxes
    /// beside large text drag it down; a heading-sized key split in two had its gap measured against
    /// twice the small boxes' height and the tail was left showing (security review, 2026-11-10). A
    /// box that spans several lines is not on one line with a word on one of them by the word's
    /// measure, so it still counts for twice the row and no more.
    /// </summary>
    internal static double TokenHeight(OcrWord a, OcrWord b, double rowHeight)
    {
        var middleA = a.Y + (a.Height / 2.0);
        var middleB = b.Y + (b.Height / 2.0);
        if (middleB >= a.Y && middleB <= a.Y + a.Height && middleA >= b.Y && middleA <= b.Y + b.Height)
        {
            return Math.Max(1, Math.Max(rowHeight, Math.Max(a.Height, b.Height)));
        }

        var tallest = 2.0 * rowHeight;
        return Math.Max(1, Math.Max(rowHeight, Math.Max(Math.Min(a.Height, tallest), Math.Min(b.Height, tallest))));
    }

    /// <summary>
    /// One box per run of masked words side by side on a row, each at least the row's median height,
    /// so a zero-height box still paints over what it read.
    /// </summary>
    private static IEnumerable<MaskedRegion> Paint(OcrPage page, HashSet<int> masked, MaskKind kind)
    {
        var words = page.Words;
        foreach (var row in masked.Select(page.RowOf).Distinct().Order())
        {
            var members = page.RowAt(row);
            var floor = members.Height;
            var run = new List<OcrWord>();
            foreach (var word in members.Members.Append(-1))
            {
                if (word >= 0 && masked.Contains(word))
                {
                    run.Add(words[word]);
                    continue;
                }

                if (run.Count > 0)
                {
                    yield return Cover(run, kind, floor);
                    run.Clear();
                }
            }
        }
    }

    private static MaskedRegion Cover(List<OcrWord> words, MaskKind kind, int floor)
    {
        var left = words.Min(w => w.X);
        var top = words.Min(w => w.Y);
        var right = words.Max(w => w.X + w.Width);
        var bottom = words.Max(w => w.Y + w.Height);
        if (bottom - top < floor)
        {
            var middle = (top + bottom) / 2.0;
            top = (int)Math.Floor(middle - (floor / 2.0));
            bottom = top + floor;
        }

        return new MaskedRegion { X = left, Y = top, Width = right - left, Height = bottom - top, Kind = kind };
    }

    private static Dictionary<MaskKind, int> Count(IEnumerable<PatternMatch> matches)
    {
        var counts = new Dictionary<MaskKind, int>();
        foreach (var match in matches)
        {
            counts[match.Kind] = counts.GetValueOrDefault(match.Kind) + 1;
        }

        return counts;
    }
}
