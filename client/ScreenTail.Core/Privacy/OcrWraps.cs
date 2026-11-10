using System.Text;

namespace ScreenTail.Core.Privacy;

/// <summary>
/// The page read a second time with every line that may have been cut by its box's edge joined to the
/// line that may continue it (ST-042, after #268 and #269).
///
/// The detectors read OCR words joined by spaces, so a key, a card number or a password that a terminal
/// or a text box wrapped is two words to them: the piece on the first row is masked only when it is long
/// enough to match by itself, and the piece on the row below never. This writes the page out again with
/// the two pieces as one word. <see cref="RedactionEngine.RedactFrame"/> runs the same detectors over it
/// and takes only the matches that cross a join, so this pass can add a mask and can never take one
/// away: whatever it gets wrong, the frame is masked at least as it was without it.
///
/// What is joined is decided on the screen and from nothing else:
/// <list type="bullet">
/// <item>A row is cut into runs wherever two words stand more than <see cref="ColumnGap"/> row heights
/// apart, so two panes side by side are two runs.</item>
/// <item>The last token of a run is joined to the first token of a run on a row below, when that row
/// starts no more than <see cref="LineGap"/> row heights under it (at most <see cref="RowsBelow"/> rows
/// are looked at), the run below starts left of where the run above ends, and it ends no more than
/// <see cref="Overhang"/> row heights past it: a line that overflowed is as wide as its box, so its
/// continuation cannot run on past it.</item>
/// <item>Of the runs that fit, the first row down with one that stands under the run above wins, and on
/// it the one whose start is nearest the start of the run above, so an indented continuation and a
/// pane whose lines sit between the other pane's are both found. With none under it, the first that
/// fits at all.</item>
/// <item>A join whose row above or below has a right-to-left letter is made the other way round as
/// well: the leftmost token above to the rightmost below.</item>
/// <item>A token that is a run by itself and is joined from above is joined on to what is below it, so
/// a value over three rows or thirty is one word.</item>
/// </list>
///
/// It does not ask whether the row above reaches any edge, because no edge is known: every run's end
/// over a run no wider is joined. What that costs is in the PR that added it and in
/// docs/security/data-flow.md: two unrelated rows whose end and start make a secret between them are
/// masked, which for a password means the row below it.
///
/// Each word is written once in its own row and at most a fixed number of times more as part of a
/// join, and each join is found by binary search, so the pass is O(n log n) in the page's words.
/// </summary>
internal sealed class OcrWraps
{
    /// <summary>
    /// The gap, in row heights, past which two words on a row are in different runs. A space measures
    /// 0.28 to 0.56 of the row's height (<see cref="RedactionEngine.TokenGap"/>), so text is never cut;
    /// lower cuts more runs, and more runs are more joins tried.
    /// </summary>
    internal const double ColumnGap = 2.0;

    /// <summary>How far under a row, in row heights, the top of the row that continues it may be.</summary>
    internal const double LineGap = 2.0;

    /// <summary>How far past the end of the run above, in row heights, its continuation may end.</summary>
    internal const double Overhang = 2.0;

    /// <summary>How many rows under a row are looked at for its continuation.</summary>
    internal const int RowsBelow = 3;

    /// <summary>
    /// How many words after the joined token are written with it: a card number cut inside a group
    /// needs the groups after the cut.
    /// </summary>
    private const int WordsAfter = 4;

    /// <summary>How many more tokens a chain takes once it reaches one a chain has already taken.</summary>
    private const int TokensPastSeen = 2;

    private readonly OcrPage _page;
    private readonly List<Run> _runs = [];
    private readonly int[] _firstRun; // per row, and one past the end
    private readonly int[][] _pairs = [[], []];
    private readonly bool[][] _seen = [[], []];
    private readonly StringBuilder _text = new();
    private readonly List<int> _starts = [];
    private readonly List<int> _ends = [];
    private readonly List<int> _words = [];
    private readonly List<int> _joins = [];

    /// <param name="Row">The row the run is on.</param>
    /// <param name="From">Its first word's place in the row.</param>
    /// <param name="To">Its last word's place in the row.</param>
    /// <param name="Left">Its left edge.</param>
    /// <param name="Right">Its right edge.</param>
    /// <param name="HeadTo">The place its first token ends at.</param>
    /// <param name="TailFrom">The place its last token starts at.</param>
    private readonly record struct Run(int Row, int From, int To, int Left, int Right, int HeadTo, int TailFrom)
    {
        public bool OneToken => HeadTo == To;
    }

    public OcrWraps(OcrPage page)
    {
        _page = page;
        _firstRun = new int[page.RowCount + 1];
        for (var row = 0; row < page.RowCount; row++)
        {
            _firstRun[row] = _runs.Count;
            Cut(row);
        }

        _firstRun[page.RowCount] = _runs.Count;
        for (var way = 0; way < 2; way++)
        {
            _pairs[way] = new int[_runs.Count];
            _seen[way] = new bool[_runs.Count];
            for (var run = 0; run < _runs.Count; run++)
            {
                _pairs[way][run] = way == 0 ? Below(run) : BelowMirrored(run);
            }
        }

        for (var row = 0; row < page.RowCount; row++)
        {
            Write(row, mirrored: false);
            Write(row, mirrored: true);
        }

        Text = _text.ToString();
    }

    /// <summary>The page with its joins made, for the detectors.</summary>
    public string Text { get; }

    /// <summary>Whether anything was joined. Where nothing was, the text holds nothing the page's own does not.</summary>
    public bool Any => _joins.Count > 0;

    /// <summary>Whether a span of <see cref="Text"/> runs across a join, with a character either side of it.</summary>
    public bool Crosses(int start, int end)
    {
        var join = UpperBound(_joins, start);
        return join < _joins.Count && _joins[join] < end;
    }

    /// <summary>The page's words that a span of <see cref="Text"/> touches.</summary>
    public IEnumerable<int> Touching(int start, int end)
    {
        for (var span = UpperBound(_ends, start); span < _starts.Count && _starts[span] < end; span++)
        {
            yield return _words[span];
        }
    }

    /// <summary>The page's word that holds the character at <paramref name="offset"/> of <see cref="Text"/>, or -1.</summary>
    public int WordAt(int offset)
    {
        var span = UpperBound(_ends, offset);
        return span < _starts.Count && _starts[span] <= offset ? _words[span] : -1;
    }

    /// <summary>The first index whose value is greater than <paramref name="value"/>, in an ascending list.</summary>
    private static int UpperBound(List<int> sorted, int value)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] <= value)
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

    /// <summary>Cuts a row into its runs, and finds each run's first and last token.</summary>
    private void Cut(int rowIndex)
    {
        var row = _page.RowAt(rowIndex);
        var words = _page.Words;
        var from = 0;
        var right = int.MinValue;
        for (var place = 0; place <= row.Members.Length; place++)
        {
            if (place < row.Members.Length)
            {
                var word = words[row.Members[place]];
                if (place == from || word.X - right <= ColumnGap * row.Height)
                {
                    right = Math.Max(right, word.X + word.Width);
                    continue;
                }
            }

            var to = place - 1;
            var headTo = from;
            while (headTo < to && OneToken(row, headTo, headTo + 1))
            {
                headTo++;
            }

            var tailFrom = to;
            while (tailFrom > from && OneToken(row, tailFrom - 1, tailFrom))
            {
                tailFrom--;
            }

            _runs.Add(new Run(rowIndex, from, to, words[row.Members[from]].X, right, headTo, tailFrom));
            if (place < row.Members.Length)
            {
                from = place;
                right = words[row.Members[place]].X + words[row.Members[place]].Width;
            }
        }
    }

    /// <summary>Whether the words at two places of a row are pieces of one token, by the token rule.</summary>
    private bool OneToken(OcrPage.Row row, int a, int b)
    {
        var (first, second) = (_page.Words[row.Members[a]], _page.Words[row.Members[b]]);
        return RedactionEngine.Neighbours(first, second, row.Height)
            && RedactionEngine.GapShare(first, second, row.Height) <= RedactionEngine.TokenGap;
    }

    /// <summary>The run that may continue <paramref name="index"/> on a row below, read left to right, or -1.</summary>
    private int Below(int index)
    {
        var run = _runs[index];
        var above = _page.RowAt(run.Row);
        var fallback = -1;
        for (var rowIndex = run.Row + 1; rowIndex < _page.RowCount && rowIndex <= run.Row + RowsBelow; rowIndex++)
        {
            var row = _page.RowAt(rowIndex);
            var height = Math.Max(above.Height, row.Height);
            if (row.Top - above.Bottom > LineGap * height)
            {
                break;
            }

            // The last run on the row that starts at or left of this one, and the one after it.
            int low = _firstRun[rowIndex], high = _firstRun[rowIndex + 1];
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (_runs[middle].Left <= run.Left)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            bool Fits(int other) =>
                other >= _firstRun[rowIndex] && other < _firstRun[rowIndex + 1]
                && _runs[other].Left < run.Right && _runs[other].Right <= run.Right + (Overhang * height);

            var (left, right) = (low - 1, low);
            var leftUnder = Fits(left) && _runs[left].Right > run.Left;
            if (Fits(right) && (!leftUnder || _runs[right].Left - run.Left < run.Left - _runs[left].Left))
            {
                return right;
            }

            if (leftUnder)
            {
                return left;
            }

            if (fallback < 0 && Fits(left))
            {
                fallback = left;
            }
        }

        return fallback;
    }

    /// <summary>
    /// The same read right to left, for a join with a right-to-left letter on either row: the run whose
    /// right end is nearest this one's, that ends right of where this one starts and starts no further
    /// left than it by more than the overhang.
    /// </summary>
    private int BelowMirrored(int index)
    {
        var run = _runs[index];
        var above = _page.RowAt(run.Row);
        var fallback = -1;
        for (var rowIndex = run.Row + 1; rowIndex < _page.RowCount && rowIndex <= run.Row + RowsBelow; rowIndex++)
        {
            var row = _page.RowAt(rowIndex);
            var height = Math.Max(above.Height, row.Height);
            if (row.Top - above.Bottom > LineGap * height)
            {
                break;
            }

            if (!above.RightToLeft && !row.RightToLeft)
            {
                continue;
            }

            // The first run on the row that ends at or right of this one, and the one before it.
            int low = _firstRun[rowIndex], high = _firstRun[rowIndex + 1];
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (_runs[middle].Right < run.Right)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            bool Fits(int other) =>
                other >= _firstRun[rowIndex] && other < _firstRun[rowIndex + 1]
                && _runs[other].Right > run.Left && _runs[other].Left >= run.Left - (Overhang * height);

            var (left, right) = (low - 1, low);
            var rightUnder = Fits(right) && _runs[right].Left < run.Right;
            if (Fits(left) && (!rightUnder || run.Right - _runs[left].Right < _runs[right].Right - run.Right))
            {
                return left;
            }

            if (rightUnder)
            {
                return right;
            }

            if (fallback < 0 && Fits(right))
            {
                fallback = right;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Writes a row: its words a space apart, and after each run's last token, with no space, the token
    /// that may continue it and the words after that. Mirrored, the row is written from its right end
    /// and only where one of its runs has a join that way.
    /// </summary>
    private void Write(int rowIndex, bool mirrored)
    {
        var way = mirrored ? 1 : 0;
        var (first, end) = (_firstRun[rowIndex], _firstRun[rowIndex + 1]);
        var joined = false;
        for (var run = first; run < end; run++)
        {
            joined |= _pairs[way][run] >= 0;
        }

        // A row with no join is in the page's own text already, as far as any detector can tell.
        if (!joined)
        {
            return;
        }

        var members = _page.RowAt(rowIndex).Members;
        for (var i = first; i < end; i++)
        {
            var run = _runs[mirrored ? end - 1 - (i - first) : i];
            var index = mirrored ? end - 1 - (i - first) : i;
            if (_pairs[way][index] < 0)
            {
                for (var place = mirrored ? run.To : run.From; place >= run.From && place <= run.To; place += mirrored ? -1 : 1)
                {
                    Word(members[place], spaced: true);
                }

                continue;
            }

            // Up to the token at the line's end, then that token as one word, its pieces left to right
            // as the engine read them, and what it is joined to.
            if (mirrored)
            {
                for (var place = run.To; place > run.HeadTo; place--)
                {
                    Word(members[place], spaced: true);
                }

                Token(members, run.From, run.HeadTo, spaced: true);
            }
            else
            {
                for (var place = run.From; place < run.TailFrom; place++)
                {
                    Word(members[place], spaced: true);
                }

                Token(members, run.TailFrom, run.To, spaced: true);
            }

            Chain(_pairs[way][index], way);
        }
    }

    /// <summary>
    /// Writes what a line's end is joined to: the token that may continue it, and where that token is a
    /// run by itself, what continues that, and so on down the page. A token already written into a
    /// chain is followed for <see cref="TokensPastSeen"/> more and no further, so a column of a
    /// thousand one-word rows is written a fixed number of times and not once from every row of it.
    /// </summary>
    private void Chain(int run, int way)
    {
        var mirrored = way == 1;
        var left = int.MaxValue;
        while (true)
        {
            var head = _runs[run];
            var members = _page.RowAt(head.Row).Members;
            _joins.Add(_text.Length);
            if (mirrored)
            {
                Token(members, head.TailFrom, head.To, spaced: false);
            }
            else
            {
                Token(members, head.From, head.HeadTo, spaced: false);
            }

            if (_seen[way][run])
            {
                left = Math.Min(left, TokensPastSeen);
            }

            _seen[way][run] = true;
            var next = _pairs[way][run];
            if (!head.OneToken || next < 0 || left == 0)
            {
                // The words after it on its row, for a rule that reads on.
                for (var word = 1; word <= WordsAfter; word++)
                {
                    var place = mirrored ? head.TailFrom - word : head.HeadTo + word;
                    if (place < head.From || place > head.To)
                    {
                        break;
                    }

                    Word(members[place], spaced: true);
                }

                return;
            }

            left = left == int.MaxValue ? left : left - 1;
            run = next;
        }
    }

    /// <summary>Writes the words at places <paramref name="from"/> to <paramref name="to"/> of a row as one word.</summary>
    private void Token(int[] members, int from, int to, bool spaced)
    {
        for (var place = from; place <= to; place++)
        {
            Word(members[place], spaced && place == from);
        }
    }

    private void Word(int word, bool spaced)
    {
        if (spaced && _text.Length > 0)
        {
            _ = _text.Append(' ');
        }

        _starts.Add(_text.Length);
        _ = _text.Append(_page.Words[word].Text);
        _ends.Add(_text.Length);
        _words.Add(word);
    }
}
