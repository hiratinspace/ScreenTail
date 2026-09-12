using System.Text.RegularExpressions;

namespace ScreenTail.Spike.Core;

/// <summary>Legibility proxy for ST-001 AC4: how many ground-truth words survive downscale + JPEG + OCR.</summary>
public static partial class OcrAgreement
{
    public static double WordRecall(string truth, string recognized)
    {
        var truthWords = Tokenize(truth);
        if (truthWords.Count == 0)
        {
            return 1.0;
        }

        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in Tokenize(recognized))
        {
            available[word] = available.GetValueOrDefault(word) + 1;
        }

        var hits = 0;
        foreach (var word in truthWords)
        {
            if (available.GetValueOrDefault(word) > 0)
            {
                hits++;
                available[word]--;
            }
        }

        return (double)hits / truthWords.Count;
    }

    public static IReadOnlyList<string> Tokenize(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
