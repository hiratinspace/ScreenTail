using System.Text;

namespace ScreenTail.Core.Privacy;

/// <summary>
/// The page read a second time with every line that may have been cut by its box's edge joined to the
/// lines that may continue it (ST-042, after #268 and #269).
///
/// The detectors read OCR words joined by spaces, so a key, a card number or a password that a terminal
/// or a text box wrapped is two words to them: the piece on the first row is masked only when it is long
/// enough to match by itself, and the piece on the row below never. This writes the page out again with
/// the two pieces as one word. <see cref="RedactionEngine.RedactFrame"/> runs the same detectors over it
/// and takes only the matches that cross a join, so this pass can add a mask and can never take one
/// away: whatever it gets wrong, the frame is masked at least as it was without it.
///
/// What is joined is decided on the screen:
/// <list type="bullet">
/// <item>A row is cut into runs wherever two words stand more than <see cref="ColumnGap"/> row heights
/// apart, and at a word with no letter or digit in it (a divider, a rule, a continuation mark), which
/// is set aside. So two panes side by side are two runs, and so is a gutter and the text after it.</item>
/// <item>The last token of a run may be joined to the first token of a run on a row below, when that
/// row starts no more than <see cref="LineGap"/> row heights under it (<see cref="RowsBelow"/> rows are
/// looked at), the run below starts left of where the run above ends, and it ends no more than
/// <see cref="Overhang"/> row heights past it: a line that overflowed is as wide as its box, so its
/// continuation cannot run on past it.</item>
/// <item>Every run that fits is tried, up to a bound: on the row next below, the
/// <see cref="RunsARow"/> that start nearest the start of the run above, and on each row after it the
/// nearest one, of the <see cref="RunsLookedAt"/> nearest on the row. So four joins at most from one
/// run's end, eight where a row may read right to left.</item>
/// <item>A join whose row above or below has a right-to-left letter is made the other way round as
/// well: the leftmost token above to the rightmost below.</item>
/// <item>A token joined from above is joined on to what is below it, where it is a run by itself or
/// the row may be read the other way from it, so a value over three rows or thirty is one word, through
/// a row with a right-to-left word too.</item>
/// <item>Each join is written twice where the two differ: with the pieces of a token run together, as
/// the token rule reads them, and with the page's own spaces, since a narrow real space reads as none
/// and a rule that needs a word to end would not find it. And once more with a hyphen or backslash
/// dropped from the end of the line above and a "&gt;" or "+" from the start of the one below, where
/// either is there.</item>
/// </list>
///
/// It does not ask whether the row above reaches any edge, because no edge is known: every run's end
/// over a run no wider is joined. What that costs is in docs/security/data-flow.md: unrelated rows
/// whose end and start make a secret between them are masked, which for a password means the row
/// below it.
///
/// One of the joins from a run's end, the first row down with a run under it, is written into its
/// row, so a rule that reads back along the row finds what it needs; the others are written with the
/// <see cref="WordsBefore"/> words before them. Each word is so written a bounded number of times and
/// each join is found by binary search: O(n log n) in the page's words, with a constant that is
/// measured in OcrWrapTests.
/// </summary>
internal sealed class OcrWraps
{
    /// <summary>
    /// The gap, in row heights, past which two words on a row are in different runs. A space measures
    /// 0.28 to 0.56 of the row's height (<see cref="RedactionEngine.TokenGap"/>), so a sentence is not
    /// cut; lower cuts more runs, and more runs are more joins tried.
    /// </summary>
    internal const double ColumnGap = 1.0;

    /// <summary>How far under a row, in row heights, the top of the row that continues it may be.</summary>
    internal const double LineGap = 2.0;

    /// <summary>How far past the end of the run above, in row heights, its continuation may end.</summary>
    internal const double Overhang = 2.0;

    /// <summary>How many rows under a row are looked at for its continuation.</summary>
    internal const int RowsBelow = 3;

    /// <summary>
    /// How many runs on the row next below are joined to one run's end, each way. On each row after
    /// that, one: the nearest that fits.
    /// </summary>
    internal const int RunsARow = 2;

    /// <summary>How many of a row's runs, nearest first, are looked at to find those.</summary>
    internal const int RunsLookedAt = 6;

    /// <summary>
    /// How many pieces of a token are written as one word at a join. A token the engine read as more
    /// pieces than this is written by the ones nearest the join, and is joined no further on.
    /// </summary>
    internal const int Pieces = 8;

    /// <summary>
    /// How many words after the joined token are written with it: a card number cut inside a group
    /// needs the groups after the cut.
    /// </summary>
    internal const int WordsAfter = 4;

    /// <summary>How many words before the line's last token are written with a join that is not written into its row.</summary>
    internal const int WordsBefore = 4;

    /// <summary>How many more tokens a chain takes once it reaches one a chain has already taken.</summary>
    internal const int TokensPastSeen = 2;

    /// <summary>
    /// How many tokens one chain joins at most. A value wrapped over more rows than this is joined in
    /// stretches of this many; a word as long as the page is what makes a pattern slow.
    /// </summary>
    internal const int TokensAChain = 32;

    /// <summary>How many more tokens a join that is not written into its row takes after the one it joins.</summary>
    internal const int TokensPastJoin = 0;

    private readonly OcrPage _page;
    private readonly List<Run> _runs = [];
    private readonly int[] _firstRun; // per row, and one past the end
    private readonly int[][] _first = [[], []]; // per way, the join written into the row
    private readonly bool[][] _seen = [[], [], [], []]; // per way and spacing
    private readonly StringBuilder _text = new();
    private readonly List<int> _starts = [];
    private readonly List<int> _ends = [];
    private readonly List<int> _words = [];
    private readonly List<int> _joins = [];
    private bool _pieces; // a token of more than one word was written
    private bool _dropped; // a continuation mark was dropped

    /// <param name="Row">The row the run is on.</param>
    /// <param name="From">Its first word's place in the row.</param>
    /// <param name="To">Its last word's place in the row.</param>
    /// <param name="Left">Its left edge.</param>
    /// <param name="Right">Its right edge.</param>
    /// <param name="HeadTo">The place its first token ends at.</param>
    /// <param name="TailFrom">The place its last token starts at.</param>
    /// <param name="Mark">Whether it is one word with no letter or digit, which is joined to nothing.</param>
    private readonly record struct Run(int Row, int From, int To, int Left, int Right, int HeadTo, int TailFrom, bool Mark)
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
            _first[way] = new int[_runs.Count];
            _seen[way * 2] = new bool[_runs.Count];
            _seen[(way * 2) + 1] = new bool[_runs.Count];
            for (var run = 0; run < _runs.Count; run++)
            {
                _first[way][run] = way == 0 ? Below(run) : BelowMirrored(run);
            }
        }

        // Each row with what its runs are first joined to, both ways and in both spacings.
        for (var row = 0; row < page.RowCount; row++)
        {
            for (var way = 0; way < 2; way++)
            {
                _pieces = false;
                Write(row, way, glued: true);
                if (_pieces)
                {
                    Write(row, way, glued: false);
                }
            }
        }

        // Then every other join, and every join again with its continuation marks dropped.
        var fits = new List<int>();
        for (var run = 0; run < _runs.Count; run++)
        {
            for (var way = 0; way < 2; way++)
            {
                Fitting(run, way, fits);
                foreach (var other in fits)
                {
                    if (other != _first[way][run])
                    {
                        Join(run, other, way, drop: false);
                    }

                    if (Marked(run, other, way))
                    {
                        Join(run, other, way, drop: true);
                    }
                }
            }
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
                if (place == from
                    || (word.X - right <= ColumnGap * row.Height && !IsMark(word.Text) && !IsMark(words[row.Members[place - 1]].Text)))
                {
                    right = Math.Max(right, word.X + word.Width);
                    continue;
                }
            }

            var to = place - 1;
            var headTo = from;
            while (headTo < to && headTo - from < Pieces - 1 && OneToken(row, headTo, headTo + 1))
            {
                headTo++;
            }

            var tailFrom = to;
            while (tailFrom > from && to - tailFrom < Pieces - 1 && OneToken(row, tailFrom - 1, tailFrom))
            {
                tailFrom--;
            }

            _runs.Add(new Run(rowIndex, from, to, words[row.Members[from]].X, right, headTo, tailFrom, IsMark(words[row.Members[from]].Text)));
            if (place < row.Members.Length)
            {
                from = place;
                right = words[row.Members[place]].X + words[row.Members[place]].Width;
            }
        }
    }

    /// <summary>
    /// A word with no letter or digit in it: a divider between panes, a gutter's rule, a backslash at
    /// a line's end or a "&gt;" at the start of its continuation. It ends the run before it and is a
    /// run by itself, which nothing is joined to.
    /// </summary>
    private static bool IsMark(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the words at two places of a row are pieces of one token, by the token rule.</summary>
    private bool OneToken(OcrPage.Row row, int a, int b)
    {
        var (first, second) = (_page.Words[row.Members[a]], _page.Words[row.Members[b]]);
        return RedactionEngine.Neighbours(first, second, row.Height)
            && RedactionEngine.GapShare(first, second, row.Height) <= RedactionEngine.TokenGap;
    }

    /// <summary>
    /// Whether run <paramref name="other"/>, on a row below, may continue <paramref name="run"/>:
    /// read left to right it starts left of where the run above ends and ends no more than the
    /// overhang past it; read right to left, the same mirrored.
    /// </summary>
    private bool Fits(Run run, int other, int rowIndex, double height, int way)
    {
        if (other < _firstRun[rowIndex] || other >= _firstRun[rowIndex + 1] || _runs[other].Mark)
        {
            return false;
        }

        return way == 0
            ? _runs[other].Left < run.Right && _runs[other].Right <= run.Right + (Overhang * height)
            : _runs[other].Right > run.Left && _runs[other].Left >= run.Left - (Overhang * height);
    }

    /// <summary>
    /// Where <paramref name="run"/> would stand among a row's runs: the first that starts right of
    /// where it starts, or read right to left, the first that ends at or right of where it ends.
    /// </summary>
    private int Place(int rowIndex, Run run, int way)
    {
        int low = _firstRun[rowIndex], high = _firstRun[rowIndex + 1];
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (way == 0 ? _runs[middle].Left <= run.Left : _runs[middle].Right < run.Right)
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
    /// Every run that may continue <paramref name="index"/> on the rows below, up to the bound: on
    /// the row next below the <see cref="RunsARow"/> nearest that fit, on each after it the nearest
    /// one, of the <see cref="RunsLookedAt"/> nearest on the row.
    /// </summary>
    private void Fitting(int index, int way, List<int> found)
    {
        found.Clear();
        var run = _runs[index];
        if (run.Mark)
        {
            return;
        }

        var above = _page.RowAt(run.Row);
        for (var rowIndex = run.Row + 1; rowIndex < _page.RowCount && rowIndex <= run.Row + RowsBelow; rowIndex++)
        {
            var row = _page.RowAt(rowIndex);
            var height = Math.Max(above.Height, row.Height);
            if (row.Top - above.Bottom > LineGap * height)
            {
                break;
            }

            if (way == 1 && !above.RightToLeft && !row.RightToLeft)
            {
                continue;
            }

            int Distance(int other) => way == 0 ? Math.Abs(_runs[other].Left - run.Left) : Math.Abs(_runs[other].Right - run.Right);

            var (first, end) = (_firstRun[rowIndex], _firstRun[rowIndex + 1]);
            var right = Place(rowIndex, run, way);
            var left = right - 1;
            var most = rowIndex == run.Row + 1 ? RunsARow : 1;
            for (int looked = 0, taken = 0; taken < most && looked < RunsLookedAt && (left >= first || right < end); looked++)
            {
                var other = right < end && (left < first || Distance(right) < Distance(left)) ? right++ : left--;
                if (Fits(run, other, rowIndex, height, way))
                {
                    found.Add(other);
                    taken++;
                }
            }
        }
    }

    /// <summary>
    /// The run whose join is written into the row of <paramref name="index"/>, read left to right, or
    /// -1: on the first row down that has one under it, the one starting nearest; with none under
    /// it, the first that fits.
    /// </summary>
    private int Below(int index)
    {
        var run = _runs[index];
        if (run.Mark)
        {
            return -1;
        }

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
            var right = Place(rowIndex, run, 0);
            var left = right - 1;
            var leftFits = Fits(run, left, rowIndex, height, 0);
            var leftUnder = leftFits && _runs[left].Right > run.Left;
            if (Fits(run, right, rowIndex, height, 0) && (!leftUnder || _runs[right].Left - run.Left < run.Left - _runs[left].Left))
            {
                return right;
            }

            if (leftUnder)
            {
                return left;
            }

            if (fallback < 0 && leftFits)
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
        if (run.Mark)
        {
            return -1;
        }

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
            var right = Place(rowIndex, run, 1);
            var left = right - 1;
            var rightFits = Fits(run, right, rowIndex, height, 1);
            var rightUnder = rightFits && _runs[right].Left < run.Right;
            if (Fits(run, left, rowIndex, height, 1) && (!rightUnder || run.Right - _runs[left].Right < _runs[right].Right - run.Right))
            {
                return left;
            }

            if (rightUnder)
            {
                return right;
            }

            if (fallback < 0 && rightFits)
            {
                fallback = right;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Writes a row: its words a space apart, and after each run's last token, with no space, the token
    /// that may continue it and the words after that. Read right to left (<paramref name="way"/> 1),
    /// the row is written from its right end and only where one of its runs has a join that way.
    /// </summary>
    /// <param name="glued">Whether the pieces of a token are written as one word, or a space apart as the page has them.</param>
    private void Write(int rowIndex, int way, bool glued)
    {
        var mirrored = way == 1;
        var (first, end) = (_firstRun[rowIndex], _firstRun[rowIndex + 1]);
        var joined = false;
        for (var run = first; run < end; run++)
        {
            joined |= _first[way][run] >= 0;
        }

        // A row with no join is in the page's own text already, as far as any detector can tell.
        if (!joined)
        {
            return;
        }

        var members = _page.RowAt(rowIndex).Members;
        for (var i = first; i < end; i++)
        {
            var index = mirrored ? end - 1 - (i - first) : i;
            var run = _runs[index];
            if (_first[way][index] < 0)
            {
                for (var place = mirrored ? run.To : run.From; place >= run.From && place <= run.To; place += mirrored ? -1 : 1)
                {
                    Word(members[place], spaced: true);
                }

                continue;
            }

            // Up to the token at the line's end, then that token, its pieces left to right as the
            // engine read them, and what it is joined to.
            if (mirrored)
            {
                for (var place = run.To; place > run.HeadTo; place--)
                {
                    Word(members[place], spaced: true);
                }

                Token(members, run.From, run.HeadTo, glued, drop: false);
            }
            else
            {
                for (var place = run.From; place < run.TailFrom; place++)
                {
                    Word(members[place], spaced: true);
                }

                Token(members, run.TailFrom, run.To, glued, drop: false);
            }

            Chain(_first[way][index], way, glued, _seen[(way * 2) + (glued ? 0 : 1)], drop: false);
        }
    }

    /// <summary>Whether the line's end has a hyphen or backslash to drop, or its continuation's start a "&gt;" or "+".</summary>
    private bool Marked(int index, int other, int way)
    {
        var (run, head) = (_runs[index], _runs[other]);
        var end = _page.Words[_page.RowAt(run.Row).Members[way == 1 ? run.HeadTo : run.To]].Text;
        var start = _page.Words[_page.RowAt(head.Row).Members[way == 1 ? head.TailFrom : head.From]].Text;
        return (end.Length > 1 && end[^1] is '-' or '\\') || (start.Length > 1 && start[0] is '>' or '+');
    }

    /// <summary>
    /// Writes one join by itself: the few words before the line's last token, the token, and what it
    /// is joined to, in both spacings where they differ.
    /// </summary>
    /// <param name="drop">
    /// Whether to drop a hyphen or backslash from the line's end and a "&gt;" or "+" from the start of
    /// its continuation. Written only where there was one to drop.
    /// </param>
    private void Join(int index, int other, int way, bool drop)
    {
        var run = _runs[index];
        var members = _page.RowAt(run.Row).Members;
        _pieces = false;
        foreach (var glued in (ReadOnlySpan<bool>)[true, false])
        {
            if (!glued && !_pieces)
            {
                return;
            }

            var (length, spans, joins) = (_text.Length, _starts.Count, _joins.Count);
            _dropped = false;

            // A full stop, so that nothing reads from one join into the next.
            _ = _text.Append(" .");
            if (way == 1)
            {
                for (var place = Math.Min(run.To, run.HeadTo + WordsBefore); place > run.HeadTo; place--)
                {
                    Word(members[place], spaced: true);
                }

                Token(members, run.From, run.HeadTo, glued, drop);
            }
            else
            {
                for (var place = Math.Max(run.From, run.TailFrom - WordsBefore); place < run.TailFrom; place++)
                {
                    Word(members[place], spaced: true);
                }

                Token(members, run.TailFrom, run.To, glued, drop);
            }

            Chain(other, way, glued, seen: null, drop);
            if (drop && !_dropped)
            {
                // Nothing to drop: the join as it stands is written already.
                _text.Length = length;
                _starts.RemoveRange(spans, _starts.Count - spans);
                _ends.RemoveRange(spans, _ends.Count - spans);
                _words.RemoveRange(spans, _words.Count - spans);
                _joins.RemoveRange(joins, _joins.Count - joins);
                return;
            }
        }
    }

    /// <summary>
    /// Writes what a line's end is joined to: the token that may continue it, and what continues that,
    /// and so on down the page. A token is joined on the same way where it is a run by itself, and the
    /// other way where its row has a join that way: read left to right the token joined to is the
    /// row's first, which is where a row read right to left ends.
    /// </summary>
    /// <param name="seen">
    /// The tokens chains written into their rows have taken. One already taken is followed for
    /// <see cref="TokensPastSeen"/> more and no further, so a column of a thousand one-word rows is
    /// written a fixed number of times and not once from every row of it. Null for a join written by
    /// itself, which takes <see cref="TokensPastJoin"/> after its first.
    /// </param>
    private void Chain(int run, int way, bool glued, bool[]? seen, bool drop)
    {
        var left = seen is null ? TokensPastJoin : TokensAChain - 1;
        while (true)
        {
            var head = _runs[run];
            var members = _page.RowAt(head.Row).Members;
            _joins.Add(_text.Length);
            if (way == 1)
            {
                Token(members, head.TailFrom, head.To, glued, drop, spaced: false);
            }
            else
            {
                Token(members, head.From, head.HeadTo, glued, drop, spaced: false);
            }

            if (seen is not null)
            {
                if (seen[run])
                {
                    left = Math.Min(left, TokensPastSeen);
                }

                seen[run] = true;
            }

            var (next, nextWay) = (-1, way);
            if (left > 0)
            {
                if (head.OneToken && _first[way][run] >= 0)
                {
                    next = _first[way][run];
                }
                else if (_first[1 - way][run] >= 0)
                {
                    (next, nextWay) = (_first[1 - way][run], 1 - way);
                }
            }

            if (next < 0)
            {
                // The words after it on its row, for a rule that reads on.
                for (var word = 1; word <= WordsAfter; word++)
                {
                    var place = way == 1 ? head.TailFrom - word : head.HeadTo + word;
                    if (place < head.From || place > head.To)
                    {
                        break;
                    }

                    Word(members[place], spaced: true);
                }

                return;
            }

            left--;
            (run, way) = (next, nextWay);
        }
    }

    /// <summary>
    /// Writes the words at places <paramref name="from"/> to <paramref name="to"/> of a row, a token's
    /// pieces: as one word, or a space apart.
    /// </summary>
    /// <param name="spaced">Whether a space comes before the first; none does where it is joined to what is before it.</param>
    private void Token(int[] members, int from, int to, bool glued, bool drop, bool spaced = true)
    {
        _pieces |= to > from;
        for (var place = from; place <= to; place++)
        {
            Word(members[place], place == from ? spaced : !glued, dropStart: drop && place == from && !spaced, dropEnd: drop && place == to);
        }
    }

    private void Word(int word, bool spaced, bool dropStart = false, bool dropEnd = false)
    {
        if (spaced && _text.Length > 0)
        {
            _ = _text.Append(' ');
        }

        var text = _page.Words[word].Text.AsSpan();
        if (dropStart && text.Length > 1 && text[0] is '>' or '+')
        {
            text = text[1..];
            _dropped = true;
        }

        if (dropEnd && text.Length > 1 && text[^1] is '-' or '\\')
        {
            text = text[..^1];
            _dropped = true;
        }

        _starts.Add(_text.Length);
        _ = _text.Append(text);
        _ends.Add(_text.Length);
        _words.Add(word);
    }
}
