using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Publish;

public enum MatchConfidence
{
    /// <summary>The same name, give or take case and spacing. Mapped without asking, and remembered.</summary>
    Exact,

    /// <summary>The same name once punctuation and a legal suffix are ignored. Offered, never assumed.</summary>
    Likely,
}

public sealed record CompanyMatch(CompanyRef Company, MatchConfidence Confidence);

/// <summary>
/// Matches a PSA company's name to a documentation platform's company (ST-097). Exact is exact; likely
/// is a human's call; anything else is nobody's guess, because a wrong guess publishes one customer's
/// runbook into another customer's knowledge base. Two likely candidates are no match for the same
/// reason.
/// </summary>
public static partial class CompanyMapper
{
    [GeneratedRegex(@"\b(ltd|limited|llc|inc|incorporated|corp|corporation|co|plc|gmbh|pty)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex LegalSuffix();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotAlphanumeric();

    public static CompanyMatch? Match(string psaCompany, IReadOnlyList<CompanyRef> companies)
    {
        ArgumentNullException.ThrowIfNull(companies);
        var wanted = Collapse(psaCompany);
        if (wanted.Length == 0)
        {
            return null;
        }

        var exact = companies.Where(c => Collapse(c.Name) == wanted).ToList();
        if (exact.Count == 1)
        {
            return new CompanyMatch(exact[0], MatchConfidence.Exact);
        }

        if (exact.Count > 1)
        {
            return null;
        }

        var wantedLoose = Loosen(psaCompany);
        var likely = companies.Where(c => Loosen(c.Name) == wantedLoose).ToList();
        return likely.Count == 1 ? new CompanyMatch(likely[0], MatchConfidence.Likely) : null;
    }

    /// <summary>Case and whitespace do not make a different company.</summary>
    private static string Collapse(string? name) =>
        string.Join(' ', (name ?? string.Empty).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Nor does punctuation or "Ltd"; but this is a likely match, not an exact one.</summary>
    private static string Loosen(string? name)
    {
        var lowered = (name ?? string.Empty).ToLowerInvariant().Replace('&', ' ').Replace(" and ", " ", StringComparison.Ordinal);
        lowered = LegalSuffix().Replace(lowered, " ");
        var folded = lowered.Normalize(NormalizationForm.FormD);
        var ascii = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                ascii.Append(ch);
            }
        }

        return NotAlphanumeric().Replace(ascii.ToString(), string.Empty);
    }
}
