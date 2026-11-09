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
        var spans = new List<(int Start, int End, OcrWord Word)>(words.Count);
        foreach (var word in words)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var start = builder.Length;
            builder.Append(word.Text);
            spans.Add((start, builder.Length, word));
        }

        var text = builder.ToString();
        var (matches, complete) = Resolve(text);
        var regions = new List<MaskedRegion>();
        var widened = new List<PatternMatch>(matches.Count);
        var rows = new Dictionary<int, double>();
        foreach (var match in matches)
        {
            // Every word the match touches is masked whole: a partly painted number is still readable.
            var first = spans.FindIndex(s => s.Start < match.End && match.Start < s.End);
            if (first < 0)
            {
                continue;
            }

            var last = spans.FindLastIndex(s => s.Start < match.End && match.Start < s.End);

            // And so is every token it touches. The engine can read one token on screen as several
            // words: "pass: Tangerine4821Wobble" came back as "Tangerine4821" and "Wobble", and the
            // password rule, which ends a value at the first plain word, masked the first and left the
            // second in the stored text and unpainted in the frame (loop-13, 2026-11-08). The words of
            // one token have no space between them on the screen, whatever the text says, so the token
            // is found on the screen: a piece Windows listed in an OcrLine of its own, or to the left in
            // a right-to-left line, is as much a part of it as the next word in the list.
            var token = Token(words, first, last, rows);

            // A password takes the rest of its row as well (owner decision 2026-11-09, #268). The token
            // rule was measured on one font at 96 DPI; the frames that matter come through a remote
            // viewer's scaling and compression (INV-5), where a split can sit wider than any measured,
            // and the token rule already masks the next word in 16 to 29% of cases, so the row costs
            // little more to read. The cue and the words before it stay, so the line still says what
            // it was. Other kinds keep the token rule alone. Keyed on the cue, not the kind, because a
            // password another rule also claimed is merged under that rule's kind.
            if (match.CueStart >= 0)
            {
                var cue = match.CueStart < 0 ? -1 : spans.FindIndex(s => s.Start <= match.CueStart && match.CueStart < s.End);
                token = [.. token.Union(RowAfter(words, first, cue)).Order()];
            }

            regions.Add(Cover([.. token.Select(i => words[i])], match.Kind));

            // And the stored text loses the same words the picture did.
            //
            // It used to lose only the matched span, so a value the pattern stopped short of kept its
            // tail: "P@ss.word1!" is one OCR word, the password rule's value ends at the full stop, and
            // ".word1!" stayed in ocr_text — painted out of the frame and sent to the model in writing
            // (2026-09-19 review). Picture and text are two views of one decision and must agree. A
            // token listed in two places in the text is replaced in both.
            var run = 0;
            for (var i = 1; i <= token.Count; i++)
            {
                if (i < token.Count && token[i] == token[i - 1] + 1)
                {
                    continue;
                }

                var start = spans[token[run]].Start;
                widened.Add(match with { Start = start, Length = spans[token[i - 1]].End - start });
                run = i;
            }
        }

        widened.Sort((a, b) => a.Start.CompareTo(b.Start));

        // The same matches that placed the boxes write the stored text. Resolving twice gave two answers
        // that were identical by construction and cost the pattern library a second full pass over every
        // frame — on the path ADR-0001 measured worst at 2012 ms against a 700 ms budget.
        return new FrameRedaction(Replace(text, Merge(widened)), regions, Count(matches), complete);
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
                CueStart = previous.CueStart >= 0 ? previous.CueStart : match.CueStart,
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
    /// The words of the token, or tokens, that words <paramref name="first"/> to <paramref name="last"/>
    /// are part of, in reading order: every word reached from them through a neighbour on the same row
    /// closer than <see cref="TokenGap"/>, whichever side it is on and wherever the engine listed it.
    /// </summary>
    private static List<int> Token(IReadOnlyList<OcrWord> words, int first, int last, Dictionary<int, double> rows)
    {
        var token = new HashSet<int>();
        var waiting = new Queue<int>();
        for (var i = first; i <= last; i++)
        {
            _ = token.Add(i);
            waiting.Enqueue(i);
        }

        while (waiting.TryDequeue(out var i))
        {
            if (!rows.TryGetValue(i, out var row))
            {
                row = RowHeight(words, words[i]);
                rows[i] = row;
            }

            for (var j = 0; j < words.Count; j++)
            {
                if (!token.Contains(j) && GapShare(words[i], words[j], row) is <= TokenGap)
                {
                    _ = token.Add(j);
                    waiting.Enqueue(j);
                }
            }
        }

        return [.. token.Order()];
    }

    /// <summary>
    /// The words on word <paramref name="first"/>'s row on the screen from it to the row's end, the way
    /// the line reads, wherever the engine listed them. The way it reads is from the box of the cue,
    /// word <paramref name="cue"/>, to the secret's: a single neighbour in the engine's list could be
    /// listed out of order and turn the row around (security review of #268). Where that cannot be
    /// told, because the cue is on another row or in the secret's own word, it fails closed and takes
    /// the whole row.
    /// </summary>
    private static IEnumerable<int> RowAfter(IReadOnlyList<OcrWord> words, int first, int cue)
    {
        var anchor = words[first];
        var known = cue >= 0 && cue != first && SameRow(words[cue], anchor);
        var backwards = known && words[cue].X > anchor.X;
        for (var j = 0; j < words.Count; j++)
        {
            if (j == first || j == cue || !SameRow(anchor, words[j]))
            {
                continue;
            }

            if (!known || (backwards ? words[j].X <= anchor.X : words[j].X >= anchor.X))
            {
                yield return j;
            }
        }
    }

    /// <summary>
    /// Whether two words share a row: the middle of either lies within the other's height. Either,
    /// because a lowercase word's box is only as tall as the x-height and sits inside its neighbours'.
    /// </summary>
    internal static bool SameRow(OcrWord a, OcrWord b)
    {
        var middleA = a.Y + (a.Height / 2.0);
        var middleB = b.Y + (b.Height / 2.0);
        return (middleB >= a.Y && middleB <= a.Y + a.Height) || (middleA >= b.Y && middleA <= b.Y + b.Height);
    }

    /// <summary>The median height of the words on <paramref name="word"/>'s row, itself included.</summary>
    internal static double RowHeight(IReadOnlyList<OcrWord> words, OcrWord word)
    {
        var heights = words.Where(w => SameRow(word, w)).Select(w => w.Height).Order().ToList();
        return heights.Count == 0 ? word.Height : heights[heights.Count / 2];
    }

    /// <summary>
    /// The space between two words' boxes as a share of the height a space is measured against, or
    /// null when they are not on one row. The space is the horizontal distance in either direction,
    /// so a right-to-left line reads the same. The height is the largest of the two words' and their
    /// row's median: a piece with no capital, ascender or digit is only x-height tall, about 0.7 of
    /// its neighbours, and measured against itself a split looked a third wider than it is (review of
    /// #268). Each of those only makes the share smaller, so each only masks more.
    /// </summary>
    internal static double? GapShare(OcrWord a, OcrWord b, double rowHeight)
    {
        var height = Math.Max(rowHeight, Math.Max(a.Height, b.Height));
        if (height <= 0 || !SameRow(a, b))
        {
            return null;
        }

        var gap = Math.Max(b.X - (a.X + a.Width), a.X - (b.X + b.Width));
        return gap / height;
    }

    private static MaskedRegion Cover(IReadOnlyList<OcrWord> words, MaskKind kind)
    {
        var left = words.Min(w => w.X);
        var top = words.Min(w => w.Y);
        var right = words.Max(w => w.X + w.Width);
        var bottom = words.Max(w => w.Y + w.Height);
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
