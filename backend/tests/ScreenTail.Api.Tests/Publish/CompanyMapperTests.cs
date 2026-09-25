using ScreenTail.Api.Providers;
using ScreenTail.Api.Publish;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// Matching a PSA company's name to a documentation platform's company (ST-097). Exact is exact; a name
/// that differs only in case, spacing, punctuation or a legal suffix is likely and still a human's call;
/// anything else is nobody's guess, because a wrong guess publishes one customer's runbook into another's
/// knowledge base.
/// </summary>
public sealed class CompanyMapperTests
{
    private static readonly IReadOnlyList<CompanyRef> Hudu =
    [
        new("7", "Acme Dental"),
        new("8", "Borough Legal Ltd"),
        new("9", "Bright Smiles"),
    ];

    [Theory]
    [InlineData("Acme Dental", "7", MatchConfidence.Exact)]
    [InlineData("acme dental", "7", MatchConfidence.Exact)]
    [InlineData("  Acme Dental  ", "7", MatchConfidence.Exact)]
    [InlineData("Borough Legal", "8", MatchConfidence.Likely)]
    [InlineData("Borough Legal, LLC", "8", MatchConfidence.Likely)]
    [InlineData("Bright-Smiles Inc.", "9", MatchConfidence.Likely)]
    public void ANameIsMatchedWithTheConfidenceItDeserves(string psaName, string huduId, MatchConfidence confidence)
    {
        var match = CompanyMapper.Match(psaName, Hudu);

        Assert.NotNull(match);
        Assert.Equal(huduId, match.Company.Id);
        Assert.Equal(confidence, match.Confidence);
    }

    [Theory]
    [InlineData("Nowhere Inc")]
    [InlineData("Acme")]
    [InlineData("")]
    public void AnythingElseIsNobodysGuess(string psaName)
    {
        Assert.Null(CompanyMapper.Match(psaName, Hudu));
    }

    [Fact]
    public void TwoLikelyMatchesAreNoMatch()
    {
        // "Acme" against "Acme Dental" and "Acme Legal" would be a coin toss.
        IReadOnlyList<CompanyRef> companies = [new("1", "Acme Dental Ltd"), new("2", "Acme Dental LLC")];

        Assert.Null(CompanyMapper.Match("Acme Dental", companies));
    }
}
