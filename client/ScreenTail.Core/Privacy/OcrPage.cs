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
        MedianHeight = PageHeight(words);

        foreach (var group in Rows(words))
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
                heights[heights.Length / 2] > 0 ? heights[heights.Length / 2] : MedianHeight,
                members.Max(i => words[i].Width),
                heights[^1],
                members.Any(i => HasRightToLeft(words[i].Text))));
        }
    }

    /// <summary>
    /// The median height of the page's boxes that have one. Where none has (review of #268 at a3cd033:
    /// a gap measured against no height joined nothing), an estimate from the glyphs: a character is
    /// about half as wide as its line is tall. Never under a pixel.
    /// </summary>
    private static int PageHeight(IReadOnlyList<OcrWord> words)
    {
        var heights = words.Where(w => w.Height > 0).Select(w => w.Height).Order().ToArray();
        if (heights.Length > 0)
        {
            return heights[heights.Length / 2];
        }

        var estimates = words.Where(w => w.Width > 0 && w.Text.Length > 0).Select(w => 2.0 * w.Width / w.Text.Length).Order().ToArray();
        return estimates.Length == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(estimates[estimates.Length / 2]));
    }

    /// <summary>
    /// The page's rows: every word joined to every word it shares a row with (RedactionEngine.SameRow),
    /// and so on through them, so no chain of words that each share a row is ever cut in two.
    /// </summary>
    /// <remarks>
    /// Comparing each word with its row's first word only was not enough (security review of #268): a
    /// small box set high in the row was the first, a piece of a secret set a little lower did not share
    /// a row with it, and the piece became a row of its own that neither the token rule nor the
    /// password's rows ever looked at. Joined through every member, a row may take in more than one line
    /// of text where one tall box spans them, which only masks more.
    ///
    /// Two words share a row when the middle of one lies within the other's height, so any word whose
    /// middle lies between theirs lies within the taller one's height too. Down the page by middle, then,
    /// a row is an unbroken run, and a word joins the earliest row before it that it shares a row with,
    /// together with every row since: the one whose last middle is at or below its top, or whose lowest
    /// bottom is at or below its middle.
    /// </remarks>
    private static List<List<int>> Rows(IReadOnlyList<OcrWord> words)
    {
        var order = Enumerable.Range(0, words.Count).OrderBy(i => Middle(words[i])).ThenBy(i => words[i].X).ToArray();
        var middles = order.Select(i => Middle(words[i])).ToArray();
        var groups = new List<List<int>>();
        var firsts = new List<int>(); // where each group starts in the order
        var lowest = new List<double>(); // the lowest bottom of any group up to and including this one
        for (var place = 0; place < order.Length; place++)
        {
            var word = words[order[place]];
            var middle = middles[place];

            // The earliest group with a member whose middle is within this word's height...
            var byMiddle = groups.Count;
            var earliest = LowerBound(middles, place, word.Y);
            if (earliest < place)
            {
                byMiddle = GroupAt(firsts, earliest);
            }

            // ...or with a member whose height takes in this word's middle.
            int low = 0, high = groups.Count;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (lowest[mid] >= middle)
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            var join = Math.Min(byMiddle, low);
            if (join == groups.Count)
            {
                groups.Add([]);
                firsts.Add(place);
                lowest.Add(join == 0 ? double.MinValue : lowest[^1]);
            }

            // Everything from that group on is one row now.
            while (groups.Count - 1 > join)
            {
                groups[^2].AddRange(groups[^1]);
                lowest[^2] = lowest[^1];
                groups.RemoveAt(groups.Count - 1);
                firsts.RemoveAt(firsts.Count - 1);
                lowest.RemoveAt(lowest.Count - 1);
            }

            groups[^1].Add(order[place]);
            lowest[^1] = Math.Max(lowest[^1], word.Y + word.Height);
        }

        return groups;
    }

    private static double Middle(OcrWord word) => word.Y + (word.Height / 2.0);

    /// <summary>The first place before <paramref name="end"/> whose middle is at least <paramref name="value"/>.</summary>
    private static int LowerBound(double[] sorted, int end, double value)
    {
        int low = 0, high = end;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (sorted[mid] < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>The group a place in the order belongs to, from where each group starts.</summary>
    private static int GroupAt(List<int> firsts, int place)
    {
        int low = 0, high = firsts.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (firsts[mid] <= place)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <param name="Members">The row's words, left to right by their left edge.</param>
    /// <param name="Lefts">Their left edges, in the same order, for the binary search.</param>
    /// <param name="Height">
    /// The height a space on the row is measured against and a mask is painted at least as tall as: its
    /// median, or the page's where that is zero. Never under a pixel.
    /// </param>
    /// <param name="RightToLeft">Whether any word on the row has a Hebrew, Arabic or other strong right-to-left letter.</param>
    internal sealed record Row(int[] Members, int[] Lefts, int Median, int Height, int MaxWidth, int MaxHeight, bool RightToLeft);

    public IReadOnlyList<OcrWord> Words { get; }

    /// <summary>The median height of the page's words, for a row whose own is zero; at least a pixel.</summary>
    public int MedianHeight { get; }

    public int RowOf(int word) => _rowOf[word];

    public Row RowAt(int row) => _rows[row];

    /// <summary>The height a space on <paramref name="word"/>'s row is measured against (<see cref="Row.Height"/>).</summary>
    public double RowHeight(int word) => _rows[_rowOf[word]].Height;

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
