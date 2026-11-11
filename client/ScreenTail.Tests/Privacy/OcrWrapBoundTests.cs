using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// What holds the wrap pass (<see cref="OcrWraps"/>) where it is: that it only ever adds masks, and that
/// none of the distances and counts it joins by can move without a test here failing. Asked for by the
/// security review of #275 (2026-11-10). See <see cref="OcrWrapTests"/> for the rule itself.
/// </summary>
public sealed class OcrWrapBoundTests
{
    private const int Height = 20;
    private const int Glyph = 10;
    private const int Space = 10;
    private const int Left = 40;

    private const string KeyStart = "sk_live_ABCDEFGHIJ";
    private const string KeyRest = "KLMNOPQRSTUV0123";

    private static readonly RedactionEngine Engine = new();

    private static readonly string[] Vocabulary =
    [
        "password:", "pwd:", "wifi", "Tangerine4821", "Wobble", KeyStart, KeyRest, KeyStart + "-", ">" + KeyRest,
        "4111", "1111", "4111111111", "111111", "41", "11", "123-45-", "6789", "123-45-6789",
        "user.name@", "example.com", "a.b@example.com", "מפתח", "סיום", "|", "\\", ">", "-", "+",
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIx", "MjM0NTY3ODkwIiwibmFtZSI6", "the", "note", "ok", "order",
        "deploy", "--key", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "Server=db;Password=", "Hunter2Hunter2;",
    ];

    /// <summary>
    /// The pass is fail-closed only if it takes nothing away. On two thousand random pages of rows,
    /// panes, marks, right-to-left words and pieces of secrets, every word the engine masks without
    /// the pass it masks with it, every box painted without it is painted with it, and no kind is
    /// counted fewer times.
    /// </summary>
    [Fact]
    public void TheWrapPassOnlyEverAddsMasks()
    {
        var policy = new RedactionPolicy { Emails = true };
        var with = new RedactionEngine(policy);
        var without = new RedactionEngine(policy) { Wraps = false };
        var added = 0;
        for (var seed = 0; seed < 2000; seed++)
        {
            var words = RandomPage(new Random(seed));

            var (bare, full) = (without.RedactFrame(words), with.RedactFrame(words));

            for (var i = 0; i < words.Count; i++)
            {
                var (before, after) = (Covered(bare, words[i]), Covered(full, words[i]));
                Assert.True(!before || after, $"page {seed}: word {i} is masked without the wrap pass and not with it");
                added += after && !before ? 1 : 0;
            }

            Assert.All(bare.Regions, region => Assert.True(
                full.Regions.Any(r => r.X <= region.X && r.Y <= region.Y && r.X + r.Width >= region.X + region.Width && r.Y + r.Height >= region.Y + region.Height),
                $"page {seed}: a box painted without the wrap pass is not painted with it"));
            Assert.All(bare.Counts, count => Assert.True(
                full.Counts.GetValueOrDefault(count.Key) >= count.Value,
                $"page {seed}: fewer of kind {count.Key} counted with the wrap pass than without it"));
        }

        // And the pages are ones it has something to do on, or the above proves nothing.
        Assert.True(added > 500, $"the wrap pass masked only {added} words more on 2,000 pages");
    }

    [Fact]
    public void TheDistancesAndCountsAreTheOnesTheDocsState()
    {
        // docs/security/data-flow.md and the bound in the PR state these. Change one and change those.
        Assert.Equal(1.0, OcrWraps.ColumnGap);
        Assert.Equal(2.0, OcrWraps.LineGap);
        Assert.Equal(2.0, OcrWraps.Overhang);
        Assert.Equal(3, OcrWraps.RowsBelow);
        Assert.Equal(2, OcrWraps.RunsARow);
        Assert.Equal(6, OcrWraps.RunsLookedAt);
        Assert.Equal(8, OcrWraps.Pieces);
        Assert.Equal(4, OcrWraps.WordsAfter);
        Assert.Equal(4, OcrWraps.WordsBefore);
        Assert.Equal(2, OcrWraps.TokensPastSeen);
        Assert.Equal(32, OcrWraps.TokensAChain);
        Assert.Equal(0, OcrWraps.TokensPastJoin);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void AContinuationMayEndTwoRowHeightsPastTheLineAboveAndNoFurther(int past, bool joined)
    {
        // The line above ends at 280.
        var above = Row(0, Left, "--key", KeyStart);
        OcrWord[] words = [.. above, Word(KeyRest, Right(above) + past - (KeyRest.Length * Glyph), 30)];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal(joined, Covered(redaction, words[1]) && Covered(redaction, words[2]));
        Assert.Equal(joined, redaction.Regions.Count > 0);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void ReadRightToLeftAContinuationMayStartTwoRowHeightsBeforeTheLineAboveAndNoFurther(int before, bool joined)
    {
        // A row with a Hebrew word ends at its left edge, 200, and goes on from the right of the row below.
        OcrWord[] words =
        [
            Word(KeyStart, 200, 0),
            Word("מפתח", 200 + (KeyStart.Length * Glyph) + 20, 0, 60),
            Word(KeyRest, 200 - before, 30),
        ];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal(joined, Covered(redaction, words[0]) && Covered(redaction, words[2]));
        Assert.Equal(joined, redaction.Regions.Count > 0);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void ARowMayStartTwoRowHeightsUnderTheLineAboveAndNoFurther(int under, bool joined)
    {
        OcrWord[] words = [.. Row(0, Left, "--key", KeyStart), Word(KeyRest, Left, Height + under)];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal(joined, Covered(redaction, words[1]) && Covered(redaction, words[2]));
        Assert.Equal(joined, redaction.Regions.Count > 0);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void ARowIsCutIntoRunsAtAGapOverOneRowHeight(int gap, bool cut)
    {
        // Cut, the key's piece is the end of a run and is joined to the row below. Not cut, the run
        // ends at the word after the gap.
        var above = Row(0, Left, "--key", KeyStart);
        OcrWord[] words = [.. above, Word("Notes", Right(above) + gap, 0), Word(KeyRest, Left, 30)];

        var redaction = Engine.RedactFrame(words);

        Assert.Equal(cut, Covered(redaction, words[1]) && Covered(redaction, words[3]));
        Assert.Equal(cut, redaction.Regions.Count > 0);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void AContinuationIsLookedForOnThreeRowsBelowAndNoMore(int between, bool joined)
    {
        // Short rows of small print between the two, all within two row heights of the line above,
        // and two words each, so that no chain runs through them to the row after.
        var words = Row(0, Left, "--key", KeyStart).ToList();
        for (var i = 0; i < between; i++)
        {
            words.Add(new OcrWord("zz", Left, 22 + (i * 8), 20, 6));
            words.Add(new OcrWord("zz", Left + 24, 22 + (i * 8), 20, 6));
        }

        words.Add(Word(KeyRest, Left, 22 + (between * 8)));

        var redaction = Engine.RedactFrame(words);

        Assert.Equal(joined, Covered(redaction, words[1]) && Covered(redaction, words[^1]));
        Assert.Equal(joined, redaction.Regions.Count > 0);
    }

    [Fact]
    public void FourWordsAfterTheJoinedTokenAreWrittenWithIt()
    {
        var words = Page([Word("aa", Left, 0, 300)], Row(1, Left, "bb", "c1", "c2", "c3", "c4", "c5"));

        var text = new OcrWraps(new OcrPage(words)).Text;

        Assert.Equal("aabb c1 c2 c3 c4", text);
    }

    [Fact]
    public void FourWordsBeforeTheLinesLastTokenAreWrittenWithAJoinNotWrittenIntoItsRow()
    {
        // The row next below is the join written into the row; the one after it is written by itself.
        var words = Page(Row(0, Left, "c1", "c2", "c3", "c4", "c5", "aa"), Row(1, Left, "bb"), Row(2, Left, "dd"));

        var text = new OcrWraps(new OcrPage(words)).Text;

        Assert.Contains(" . c2 c3 c4 c5 aadd", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c1 c2 c3 c4 c5 aadd", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChainThatReachesATokenAlreadyTakenGoesTwoTokensOn()
    {
        // A column of one-word rows. The first row's chain takes them all; the second row's reaches
        // one taken at once and is written two tokens past it, not to the foot of the column.
        var words = Page([.. Enumerable.Range(0, 10).Select(line => Row(line, Left, $"a{line}"))]);

        var written = new OcrWraps(new OcrPage(words)).Text.Split(' ');

        Assert.Contains("a0a1a2a3a4a5a6a7a8a9", written);
        Assert.Contains("a1a2a3a4", written);
        Assert.DoesNotContain("a1a2a3a4a5", written);
        Assert.DoesNotContain("a1a2a3", written);
    }

    private static List<OcrWord> RandomPage(Random random)
    {
        int[] gaps = [4, 6, 10, 10, 10, 18, 30, 80, 300];
        int[] heights = [20, 20, 20, 14, 28];
        int[] steps = [22, 30, 30, 30, 50, 90];
        int[] lefts = [40, 40, 40, 100, 300];
        var words = new List<OcrWord>();
        var y = 0;
        var rows = random.Next(2, 12);
        for (var row = 0; row < rows; row++)
        {
            var x = lefts[random.Next(lefts.Length)];
            var count = random.Next(1, 8);
            for (var i = 0; i < count; i++)
            {
                var text = random.Next(6) == 0
                    ? random.Next(10, 99999999).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : Vocabulary[random.Next(Vocabulary.Length)];
                var height = heights[random.Next(heights.Length)];
                words.Add(new OcrWord(text, x, y + random.Next(3), Math.Max(8, text.Length * Glyph), height));
                x += words[^1].Width + gaps[random.Next(gaps.Length)];
            }

            y += steps[random.Next(steps.Length)];
        }

        // The engine lists a page in its own order, which is not always the reading order.
        if (random.Next(4) == 0)
        {
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(words));
        }

        return words;
    }

    private static OcrWord Word(string text, int x, int y, int? width = null) => new(text, x, y, width ?? text.Length * Glyph, Height);

    private static int Right(OcrWord[] row) => row[^1].X + row[^1].Width;

    private static OcrWord[] Row(int line, int x, params string[] texts)
    {
        var row = new OcrWord[texts.Length];
        for (var i = 0; i < texts.Length; i++)
        {
            row[i] = Word(texts[i], x, line * 30);
            x += row[i].Width + Space;
        }

        return row;
    }

    private static OcrWord[] Page(params OcrWord[][] rows) => [.. rows.SelectMany(r => r)];

    private static bool Covered(FrameRedaction redaction, OcrWord word) =>
        redaction.Regions.Any(r =>
            r.X <= word.X && r.Y <= word.Y
            && r.X + r.Width >= word.X + word.Width
            && r.Y + r.Height >= word.Y + word.Height);
}
