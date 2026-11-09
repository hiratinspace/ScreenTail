namespace ScreenTail.Core.Privacy;

/// <summary>
/// The words of one OCR result as they lie on the screen: grouped into rows by their vertical middle,
/// each row sorted left to right, with its median height worked out once. Built once a frame, so the
/// token and row rules find a word's row and its neighbours by binary search rather than by scanning
/// the page for every word they look at (review of #268: a 5,000 word frame went from 12 ms to 146 ms,
/// and 2,500 password cues to 2 s, against a 700 ms budget).
/// </summary>
internal sealed class OcrPage
{
    private readonly int[] _rowOf;
    private readonly List<Row> _rows = [];

    public OcrPage(IReadOnlyList<OcrWord> words)
    {
        Words = words;
        _rowOf = new int[words.Count];

        // Down the page by vertical middle; a word starts a new row when it does not share one with the
        // row's first word (RedactionEngine.SameRow).
        var order = Enumerable.Range(0, words.Count).OrderBy(i => words[i].Y + (words[i].Height / 2.0)).ThenBy(i => words[i].X).ToList();
        var groups = new List<List<int>>();
        var seed = -1;
        foreach (var i in order)
        {
            if (seed < 0 || !RedactionEngine.SameRow(words[seed], words[i]))
            {
                groups.Add([]);
                seed = i;
            }

            groups[^1].Add(i);
        }

        foreach (var group in groups)
        {
            var members = group.OrderBy(i => words[i].X).ThenBy(i => i).ToArray();
            var heights = members.Select(i => words[i].Height).Order().ToArray();
            for (var place = 0; place < members.Length; place++)
            {
                _rowOf[members[place]] = _rows.Count;
            }

            _rows.Add(new Row(
                members,
                [.. members.Select(i => words[i].X)],
                heights[heights.Length / 2],
                members.Max(i => words[i].Width),
                heights[^1],
                members.Any(i => HasRightToLeft(words[i].Text))));
        }

        var all = words.Select(w => w.Height).Order().ToArray();
        MedianHeight = all.Length == 0 ? 0 : all[all.Length / 2];
    }

    /// <param name="Members">The row's words, left to right by their left edge.</param>
    /// <param name="Lefts">Their left edges, in the same order, for the binary search.</param>
    /// <param name="RightToLeft">Whether any word on the row has a Hebrew, Arabic or other strong right-to-left letter.</param>
    internal sealed record Row(int[] Members, int[] Lefts, int Median, int MaxWidth, int MaxHeight, bool RightToLeft);

    public IReadOnlyList<OcrWord> Words { get; }

    /// <summary>The median height of every word on the page, for a row whose own is zero.</summary>
    public int MedianHeight { get; }

    public int RowOf(int word) => _rowOf[word];

    public Row RowAt(int row) => _rows[row];

    /// <summary>The median height of the words on <paramref name="word"/>'s row, itself included.</summary>
    public double RowHeight(int word) => _rows[_rowOf[word]].Median;

    /// <summary>
    /// The other words on <paramref name="word"/>'s row whose box comes within <paramref name="reach"/>
    /// pixels of its box, on either side.
    /// </summary>
    public IEnumerable<int> Near(int word, double reach)
    {
        var row = _rows[_rowOf[word]];
        var box = Words[word];

        // A word that ends within reach starts no further left than its own width before that.
        var from = Array.BinarySearch(row.Lefts, (int)Math.Floor(box.X - reach - row.MaxWidth));
        from = from < 0 ? ~from : from;
        while (from > 0 && row.Lefts[from - 1] >= box.X - reach - row.MaxWidth)
        {
            from--;
        }

        for (var place = from; place < row.Members.Length && row.Lefts[place] <= box.X + box.Width + reach; place++)
        {
            var other = row.Members[place];
            if (other == word)
            {
                continue;
            }

            var near = Words[other];
            if (Math.Max(near.X - (box.X + box.Width), box.X - (near.X + near.Width)) <= reach)
            {
                yield return other;
            }
        }
    }

    /// <summary>
    /// Whether the text has a letter of a script written right to left: Hebrew, Arabic, Syriac, Thaana,
    /// N'Ko and the rest of that block, their presentation forms, the right-to-left marks, and the
    /// historic right-to-left scripts outside the basic plane.
    /// </summary>
    internal static bool HasRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var c = rune.Value;
            if (c is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
                or 0x200F or 0x202B or 0x202E or 0x2067
                or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF))
            {
                return true;
            }
        }

        return false;
    }
}
