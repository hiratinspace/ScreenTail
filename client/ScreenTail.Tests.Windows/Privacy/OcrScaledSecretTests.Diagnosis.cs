using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// Where a secret that is not masked whole was lost, stage by stage (ST-042, after #275). Every secret
/// the probe draws is put in the first of these it fails:
/// <list type="bullet">
/// <item><b>not read</b>: on some piece of it the engine returned no word at all;</item>
/// <item><b>misread</b>: words were returned, but what they say is not what was drawn, and the
/// character errors are counted by class;</item>
/// <item><b>no rule</b>: what was drawn was read, and no mask touches it;</item>
/// <item><b>mask short</b>: it was read, a mask touches it, and some of it is still showing.</item>
/// </list>
/// The text is synthetic, so a few misreads are printed as examples; nothing from a real screen is.
/// </summary>
public sealed partial class OcrScaledSecretTests
{
    private static readonly string[] Stages = ["masked", "not read", "misread", "no rule", "mask short"];

    private static readonly string[] ErrorClasses =
        ["underscore", "space added", "space lost", "O/0", "l/1/I", "case", "dropped", "added", "other", "spaces only"];

    private delegate Task<Reading> Reader(byte[] image);

    /// <param name="Words">What each pass read; the stored text is the first's.</param>
    /// <param name="Redaction">What the engine masks over all of them.</param>
    /// <param name="OcrMs">How long the engine took to read, every pass together.</param>
    private sealed record Reading(IReadOnlyList<OcrWord>[] Words, FrameRedaction Redaction, double OcrMs);

    private static Reader Read(WindowsOcrRecogniser recogniser) => async image =>
    {
        var clock = Stopwatch.StartNew();
        var words = (await recogniser.ReadAsync(image, TestContext.Current.CancellationToken)).Words;
        var ms = clock.Elapsed.TotalMilliseconds;
        return new Reading([words], new RedactionEngine().RedactFrame(words), ms);
    };

    /// <summary>Two readings of one frame with the masks of both: what a second pass would add.</summary>
    private static Reading Union(Reading first, Reading second) => new(
        [.. first.Words, .. second.Words],
        first.Redaction with
        {
            Regions = [.. first.Redaction.Regions, .. second.Redaction.Regions],
            Complete = first.Redaction.Complete && second.Redaction.Complete,
        },
        first.OcrMs + second.OcrMs);

    /// <summary>The words over a piece: any overlap across, their middle inside it down, left to right.</summary>
    private static List<OcrWord> On(Rectangle piece, IReadOnlyList<OcrWord> words) =>
        [.. words.Where(w => w.X < piece.Right && w.X + w.Width > piece.Left && Middle(w) >= piece.Top && Middle(w) < piece.Bottom).OrderBy(w => w.X)];

    private static double Middle(OcrWord word) => word.Y + (word.Height / 2.0);

    private static bool Overlaps(Rectangle piece, MaskedRegion region) =>
        region.X < piece.Right && region.X + region.Width > piece.Left && region.Y < piece.Bottom && region.Y + region.Height > piece.Top;

    /// <summary>
    /// The fewest edits that turn <paramref name="truth"/> into some stretch of <paramref name="read"/>,
    /// and which classes of error those edits are.
    /// </summary>
    private static (int Distance, bool[] Errors) Align(string truth, string read)
    {
        var (n, m) = (truth.Length, read.Length);
        var cost = new int[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
        {
            cost[i, 0] = i;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var same = truth[i - 1] == read[j - 1] ? 0 : 1;
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + same, Math.Min(cost[i - 1, j], cost[i, j - 1]) + 1);
            }
        }

        var end = 0;
        for (var j = 1; j <= m; j++)
        {
            end = cost[n, j] < cost[n, end] ? j : end;
        }

        var errors = new bool[ErrorClasses.Length];
        var spacesOnly = true;
        void Mark(int error)
        {
            errors[error] = true;
            spacesOnly &= error is 1 or 2;
        }

        var (a, b) = (n, end);
        while (a > 0)
        {
            if (b > 0 && cost[a, b] == cost[a - 1, b - 1] + (truth[a - 1] == read[b - 1] ? 0 : 1))
            {
                var (t, r) = (truth[a - 1], read[b - 1]);
                if (t != r)
                {
                    Mark(t == '_' ? 0
                        : r == ' ' ? 1
                        : t == ' ' ? 2
                        : "O0oQD".Contains(t, StringComparison.Ordinal) && "O0oQD".Contains(r, StringComparison.Ordinal) ? 3
                        : "lI1i|!".Contains(t, StringComparison.Ordinal) && "lI1i|!".Contains(r, StringComparison.Ordinal) ? 4
                        : char.ToLowerInvariant(t) == char.ToLowerInvariant(r) ? 5
                        : 8);
                }

                (a, b) = (a - 1, b - 1);
            }
            else if (cost[a, b] == cost[a - 1, b] + 1)
            {
                Mark(truth[a - 1] == '_' ? 0 : truth[a - 1] == ' ' ? 2 : 6);
                a--;
            }
            else
            {
                Mark(read[b - 1] == ' ' ? 1 : 7);
                b--;
            }
        }

        errors[9] = spacesOnly && cost[n, end] > 0;
        return (cost[n, end], errors);
    }

    /// <summary>A table of where secrets were lost, by stage and by kind, and the misreads by class and by kind.</summary>
    private static string StageTable(string title, IEnumerable<(string Condition, Tally Tally)> rows)
    {
        var lines = new List<string>
        {
            title + " Each cell is by kind (password, token, card, ssn, wrapped key).",
            "| condition | " + string.Join(" | ", Stages) + " | misread: " + string.Join(" | ", ErrorClasses) + " |",
            "|---|" + string.Concat(Enumerable.Repeat("---|", Stages.Length + ErrorClasses.Length)),
        };
        var samples = new List<string>();
        foreach (var (condition, tally) in rows)
        {
            lines.Add($"| {condition} | "
                + string.Join(" | ", tally.StageByKind.Select(s => string.Join(",", s))) + " | "
                + string.Join(" | ", tally.ErrorByKind.Select(e => string.Join(",", e))) + " |");
            samples.AddRange(tally.Samples.Select(s => $"  {condition}: {s}"));
        }

        if (samples.Count > 0)
        {
            lines.Add("Examples of synthetic secrets not masked whole (what was drawn, then every word read on its rows):");
            lines.AddRange(samples);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed partial class Tally
    {
        public double OcrMs { get; set; }

        public int SampleCap { get; init; } = 3;

        /// <summary>Per stage (<see cref="Stages"/>), per kind.</summary>
        public int[][] StageByKind { get; } = [.. Stages.Select(_ => new int[Kinds.Length])];

        /// <summary>Of the misread, per class of error (<see cref="ErrorClasses"/>), per kind.</summary>
        public int[][] ErrorByKind { get; } = [.. ErrorClasses.Select(_ => new int[Kinds.Length])];

        public List<string> Samples { get; } = [];

        public void Diagnose(Secret secret, double share, Reading reading, IReadOnlyList<MaskedRegion> regions)
        {
            var stage = 0;
            if (share < 0.97)
            {
                var truth = string.Join(' ', secret.PieceTexts);
                var (exact, read, distance) = (false, false, int.MaxValue);
                bool[]? errors = null;
                foreach (var words in reading.Words)
                {
                    var pieces = secret.Pieces.Select(p => On(p, words)).ToList();
                    if (pieces.Any(p => p.Count == 0))
                    {
                        continue;
                    }

                    read = true;
                    var text = string.Join(' ', pieces.Select(p => string.Join(' ', p.Select(w => w.Text))));
                    if (text.Contains(truth, StringComparison.Ordinal))
                    {
                        exact = true;
                        break;
                    }

                    var (d, e) = Align(truth, text);
                    if (d < distance)
                    {
                        (distance, errors) = (d, e);
                    }
                }

                stage = !read ? 1 : !exact ? 2 : !secret.Pieces.Any(p => regions.Any(r => Overlaps(p, r))) ? 3 : 4;
                if (stage == 2)
                {
                    for (var c = 0; c < ErrorClasses.Length; c++)
                    {
                        ErrorByKind[c][secret.Kind] += errors![c] ? 1 : 0;
                    }
                }

                if (Samples.Count < SampleCap)
                {
                    var rows = secret.Pieces.Select(p => string.Join(' ', reading.Words[0]
                        .Where(w => Middle(w) >= p.Top && Middle(w) < p.Bottom).OrderBy(w => w.X).Select(w => w.Text)));
                    Samples.Add(string.Create(CultureInfo.InvariantCulture, $"{Kinds[secret.Kind]} [{Stages[stage]}, {share:0%} covered] \"{truth}\" read \"{string.Join(" / ", rows)}\""));
                }
            }

            StageByKind[stage][secret.Kind]++;
        }

        private void AddDiagnosis(Tally other)
        {
            OcrMs += other.OcrMs;
            for (var s = 0; s < Stages.Length; s++)
            {
                for (var k = 0; k < Kinds.Length; k++)
                {
                    StageByKind[s][k] += other.StageByKind[s][k];
                }
            }

            for (var c = 0; c < ErrorClasses.Length; c++)
            {
                for (var k = 0; k < Kinds.Length; k++)
                {
                    ErrorByKind[c][k] += other.ErrorByKind[c][k];
                }
            }

            Samples.AddRange(other.Samples);
        }
    }
}
