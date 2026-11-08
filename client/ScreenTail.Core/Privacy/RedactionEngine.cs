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
            // one token have no space between them on the screen, whatever the text says.
            while (first > 0 && SameToken(spans[first - 1].Word, spans[first].Word))
            {
                first--;
            }

            while (last < spans.Count - 1 && SameToken(spans[last].Word, spans[last + 1].Word))
            {
                last++;
            }

            var touched = spans.GetRange(first, last - first + 1);
            regions.Add(Cover([.. touched.Select(s => s.Word)], match.Kind));

            // And the stored text loses the same words the picture did.
            //
            // It used to lose only the matched span, so a value the pattern stopped short of kept its
            // tail: "P@ss.word1!" is one OCR word, the password rule's value ends at the full stop, and
            // ".word1!" stayed in ocr_text — painted out of the frame and sent to the model in writing
            // (2026-09-19 review). Picture and text are two views of one decision and must agree.
            widened.Add(match with { Start = touched[0].Start, Length = touched[^1].End - touched[0].Start });
        }

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

            // Overlapping. Grow the one already kept to cover both, unless it already does.
            var previous = resolved[^1];
            if (match.End > previous.End)
            {
                resolved[^1] = previous with { Length = match.End - previous.Start };
            }
        }

        return (resolved, complete);
    }

    /// <summary>
    /// The widest gap, as a share of the shorter word's height, that two OCR words can have between their
    /// boxes and still be one token on the screen.
    ///
    /// Measured on Windows OCR (ScreenTail.Tests.Windows OcrSplitSecretTests, hosted runner, 2026-11-08):
    /// inside a token the engine split, 0.04 to 0.33 at 24 pt and 0.09 to 0.27 at 11 pt; at a real
    /// space, never under 0.36 in 15,000. The line sits just under the narrowest space, on the side that
    /// masks more: a token left half masked is a secret stored, a space taken for none is one more word
    /// under the marker. Text smaller than about 10 pt, where a space is three pixels, is joined more
    /// often than it should be, which costs readability there and never a secret.
    /// </summary>
    internal const double TokenGap = 0.35;

    /// <summary>
    /// Whether <paramref name="next"/> carries on the token <paramref name="word"/> is part of: on the
    /// same row, to its right, with less between their boxes than a space leaves.
    /// </summary>
    private static bool SameToken(OcrWord word, OcrWord next)
    {
        var height = Math.Min(word.Height, next.Height);
        if (height <= 0 || next.X < word.X)
        {
            return false;
        }

        var middle = next.Y + (next.Height / 2.0);
        if (middle < word.Y || middle > word.Y + word.Height)
        {
            return false;
        }

        return next.X - (word.X + word.Width) <= height * TokenGap;
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
