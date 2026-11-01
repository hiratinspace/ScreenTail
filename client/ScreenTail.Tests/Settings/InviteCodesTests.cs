using System.Globalization;
using System.Text.RegularExpressions;
using ScreenTail.Core.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// The invite code's shape on the client (ST-010; ADR-0009, "Guessing invite codes"): twenty characters
/// from the backend's alphabet in four groups of five, typed however a person types it, with the old
/// ten-character codes taken until they expire. The constants are copied from the backend by hand, so a
/// test reads the backend's and fails when the two drift apart.
/// </summary>
public sealed partial class InviteCodesTests
{
    [Theory]
    [InlineData("kx7pm4r2wq9htceb3nva", "KX7PM-4R2WQ-9HTCE-B3NVA")]
    [InlineData(" KX7PM 4R2WQ\t9HTCE-B3NVA ", "KX7PM-4R2WQ-9HTCE-B3NVA")]
    [InlineData("KX7PM--4R2WQ--9HTCE--B3NVA", "KX7PM-4R2WQ-9HTCE-B3NVA")]
    [InlineData("kx7pm-4r2wq", "KX7PM-4R2WQ")]
    [InlineData("kx7pm4r", "KX7PM-4R")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ACodeIsFormattedAsTheInviteShowsIt(string? typed, string formatted) =>
        Assert.Equal(formatted, InviteCodes.Format(typed));

    [Theory]
    [InlineData("KX7PM4R2WQ9HTCEB3NVA", true)]
    [InlineData("KX7PM4R2WQ", true)]
    [InlineData("KX7PM4R2WQ9HTCEB3NV", false)]
    [InlineData("KX7PM4R2WQ9HTCEB3NVAA", false)]
    [InlineData("KX7PM4R2WQ9HTCEB3NV0", false)]
    [InlineData("KX7PM4R2WQ9HTCEB3NVL", false)]
    [InlineData("", false)]
    public void OnlyTodaysLengthOrTheOldOneInTheAlphabetIsComplete(string normalised, bool complete) =>
        Assert.Equal(complete, InviteCodes.IsComplete(normalised));

    [Fact]
    public void TheExampleIsACodeOfTodaysShapeAndTheAlphabetHasNoLookAlikes()
    {
        Assert.True(InviteCodes.IsComplete(InviteCodes.Normalise(InviteCodes.Example)));
        Assert.Equal(InviteCodes.Length, InviteCodes.Normalise(InviteCodes.Example).Length);
        Assert.Equal(InviteCodes.Example, InviteCodes.Format(InviteCodes.Example));
        Assert.Equal(31, InviteCodes.Alphabet.Distinct().Count());
        Assert.DoesNotContain(InviteCodes.Alphabet, c => "01ILO".Contains(c, StringComparison.Ordinal));
        Assert.True(InviteCodes.Length * Math.Log2(InviteCodes.Alphabet.Length) >= 80);
    }

    [Fact]
    public void TheShapeIsTheBackendsShape()
    {
        var invites = File.ReadAllText(RepoFile("backend", "src", "ScreenTail.Api", "Tenancy", "Invites.cs"));

        Assert.Equal(InviteCodes.Alphabet, Constant(invites, "string Alphabet"));
        Assert.Equal(InviteCodes.Length, int.Parse(Constant(invites, "int CodeLength"), CultureInfo.InvariantCulture));
        Assert.Equal(InviteCodes.LegacyLength, int.Parse(Constant(invites, "int LegacyCodeLength"), CultureInfo.InvariantCulture));
        Assert.Equal(InviteCodes.GroupLength, int.Parse(Constant(invites, "int GroupLength"), CultureInfo.InvariantCulture));
    }

    [GeneratedRegex("""const (?<decl>\w+ \w+) = "?(?<value>[^";]+)"?;""")]
    private static partial Regex ConstantDeclaration();

    private static string Constant(string source, string declaration) =>
        ConstantDeclaration().Matches(source).Single(m => m.Groups["decl"].Value == declaration).Groups["value"].Value;

    private static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate) && Directory.Exists(Path.Combine(directory.FullName, "client")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
