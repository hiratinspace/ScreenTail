using System.Text;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <param name="Text">OCR text for one word, with the box it came from (ST-041 supplies these).</param>
public sealed record OcrWord(string Text, int X, int Y, int Width, int Height);

/// <param name="Text">The text with every match replaced by its label.</param>
/// <param name="Counts">How many of each kind were found — the only thing that may be logged (INV-10).</param>
public sealed record ScrubResult(string Text, IReadOnlyDictionary<MaskKind, int> Counts)
{
    public bool Changed => Counts.Count > 0;

    public int Total => Counts.Values.Sum();
}

/// <param name="Regions">Boxes to paint over in the stored frame, before it is readable by anything else (INV-1).</param>
public sealed record FrameRedaction(string Text, IReadOnlyList<MaskedRegion> Regions, IReadOnlyDictionary<MaskKind, int> Counts);

/// <summary>
/// Pattern-based redaction (ST-042). Two entry points: <see cref="ScrubText"/> for transcript segments and any
/// text on its way to a draft, and <see cref="RedactFrame"/> for an OCR result, which additionally reports the
/// boxes to mask in the image. The engine is pure: the caller writes the masked image and the scrubbed text.
/// </summary>
public sealed class RedactionEngine(RedactionPolicy? policy = null)
{
    private readonly RedactionPolicy _policy = policy ?? RedactionPolicy.Default;

    public ScrubResult ScrubText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = Resolve(text);
        if (matches.Count == 0)
        {
            return new ScrubResult(text, new Dictionary<MaskKind, int>());
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var match in matches)
        {
            builder.Append(text, cursor, match.Start - cursor).Append(match.Replacement);
            cursor = match.End;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return new ScrubResult(builder.ToString(), Count(matches));
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
        var matches = Resolve(text);
        var regions = new List<MaskedRegion>();
        foreach (var match in matches)
        {
            // Every word the match touches is masked whole: a partly painted number is still readable.
            var touched = spans.Where(s => s.Start < match.End && match.Start < s.End).Select(s => s.Word).ToList();
            if (touched.Count > 0)
            {
                regions.Add(Cover(touched, match.Kind));
            }
        }

        return new FrameRedaction(ScrubText(text).Text, regions, Count(matches));
    }

    /// <summary>Sorted, non-overlapping matches: earliest first, and the longest wins a tie.</summary>
    private List<PatternMatch> Resolve(string text)
    {
        var found = PatternLibrary.Find(text, _policy)
            .OrderBy(m => m.Start)
            .ThenByDescending(m => m.Length)
            .ToList();

        var resolved = new List<PatternMatch>(found.Count);
        var end = 0;
        foreach (var match in found)
        {
            if (match.Start >= end)
            {
                resolved.Add(match);
                end = match.End;
            }
        }

        return resolved;
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
