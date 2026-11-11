using System.Diagnostics;
using ScreenTail.Core.Privacy;

namespace ScreenTail.Tests.Privacy;

/// <summary>
/// What the wrap pass (<see cref="OcrWraps"/>) costs, against the 700 ms a frame may take. The budget
/// is for a frame redacted by itself, as the worker redacts it, so these run with no other test beside
/// them: timed inside the parallel suite the same frame took four times as long on the Mac that wrote
/// this, which measures the suite and not the pass.
/// </summary>
[Collection(Alone)]
public sealed class OcrWrapCostTests
{
    private const string Alone = "Timed with nothing running beside it";

    private static readonly RedactionEngine Engine = new(new RedactionPolicy { Emails = true });

    /// <summary>
    /// The wrap pass reads the page a second time, and what it reads is longer than the page: every
    /// run's end is written with up to four continuations, in two spacings where they differ. Measured
    /// on these frames (2026-11-10), the text it gives the detectors is 1.3 times the page's on prose
    /// with ordinary spaces, 4 to 8 times on a grid where every cell is a run of its own, and 15 times
    /// where every row has a right-to-left word and is read both ways. So it is a constant times the
    /// page, not as much again, and these hold the whole frame to the budget on the shapes that cost
    /// most: every row a wrap, every cell a run, addresses in every cell with the email rule on, a
    /// right-to-left word in every other cell, and a mark or a continuation mark at every word.
    /// </summary>
    [Theory]
    [InlineData("8,000 words, every row a wrap of the one above")]
    [InlineData("5,000 cells, every one a column of its own")]
    [InlineData("5,000 words one to a row")]
    [InlineData("email addresses, every one a column of its own")]
    [InlineData("5,000 words, every other one right to left")]
    [InlineData("5,000 words, dividers and continuation marks throughout")]
    public void ABigFrameOfWrapsRedactsWithinBudget(string frame)
    {
        var words = frame switch
        {
            "8,000 words, every row a wrap of the one above" => Grid(8000, 40, 50, (row, col) => col == 39 ? $"sk_live_AB{row}" : col == 0 ? $"CD{row}EF" : $"w{row}x{col}"),
            "5,000 cells, every one a column of its own" => Grid(5000, 20, 200, (row, col) => $"{row * 37 % 10}{((row * 20) + col) % 100000:00000}"),
            "email addresses, every one a column of its own" => Grid(EmailCells, 10, 200, (row, col) => $"user{row}.{col}@example{col}.com"),
            "5,000 words, every other one right to left" => Grid(5000, 20, 80, (row, col) => col % 2 == 0 ? "מפתח" : $"w{row}x{col}"),
            "5,000 words, dividers and continuation marks throughout" => Grid(5000, 20, 60, (row, col) => (col % 3) switch { 0 => "|", 1 => $"ab{row}-", _ => $">cd{col}" }),
            _ => Grid(5000, 1, 50, (row, _) => $"{row % 10}{row % 100000:00000}"),
        };

        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var clock = Stopwatch.StartNew();
            _ = Engine.RedactFrame(words);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        Assert.True(best < 700, $"{frame}: {best:0} ms, over the 700 ms a frame may take");
    }

    /// <summary>
    /// How many addresses the email frame holds: 48,000 characters of page text, as much as the
    /// largest of the other frames. Not 5,000, and that is a limit to state, not to hide: 5,000
    /// addresses a column each took 295 ms in the release build on the Mac that wrote this (85 ms
    /// without the wrap pass), which leaves a slower machine little room under the budget, and the
    /// debug build took 753 ms. No screen holds 120,000 characters, but the pass costs what the page
    /// is long, and nothing here holds a page that long to the budget.
    /// </summary>
    private const int EmailCells = 2000;

    /// <summary>Twice the words may cost about twice the time, never four times.</summary>
    [Fact]
    public void TheWrapPassGrowsWithThePageNotItsSquare()
    {
        static double Best(List<OcrWord> words)
        {
            var best = double.MaxValue;
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                _ = Engine.RedactFrame(words);
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }

            return best;
        }

        // Every cell a column of its own, one above the other: the longest chains of rows there are.
        var small = Grid(4000, 4, 200, (row, col) => $"{row % 10}{((row * 4) + col) % 100000:00000}");
        var large = Grid(16000, 4, 200, (row, col) => $"{row % 10}{((row * 4) + col) % 100000:00000}");
        _ = Best(small);

        var (few, many) = (Best(small), Best(large));

        Assert.True(many < Math.Max(40, few * 10), $"4,000 words took {few:0.0} ms and 16,000 took {many:0.0} ms: four times the words cost more than ten times the time");
    }

    private static List<OcrWord> Grid(int count, int columns, int pitch, Func<int, int, string> text)
    {
        var words = new List<OcrWord>(count);
        for (var i = 0; i < count; i++)
        {
            var (row, col) = (i / columns, i % columns);
            words.Add(new(text(row, col), col * pitch, row * 30, 45, 20));
        }

        return words;
    }
}

[CollectionDefinition("Timed with nothing running beside it", DisableParallelization = true)]
public sealed class TimedAlone;
