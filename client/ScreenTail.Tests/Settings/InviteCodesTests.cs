using System.Text.Json;
using ScreenTail.Core.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// The invite code's shape on the client (ST-010; ADR-0009, "Guessing invite codes"): twenty characters
/// from the backend's alphabet in four groups of five, typed however a person types it, with the old
/// ten-character codes taken until they expire. The rules are copied from the backend by hand, so both
/// sides run the same table, <c>shared/contracts/invite-code.v1.json</c> (the backend's half is
/// <c>InviteCodeContractTests</c>): a change to how either side reads a typed code fails one of them.
/// </summary>
public sealed class InviteCodesTests
{
    public static TheoryData<int> Cases() => [.. Enumerable.Range(0, Contract().GetProperty("cases").GetArrayLength())];

    [Fact]
    public void TheShapeIsTheContracts()
    {
        var contract = Contract();
        Assert.Equal(InviteCodes.Alphabet, contract.GetProperty("alphabet").GetString());
        Assert.Equal(InviteCodes.Length, contract.GetProperty("length").GetInt32());
        Assert.Equal(InviteCodes.LegacyLength, contract.GetProperty("legacyLength").GetInt32());
        Assert.Equal(InviteCodes.GroupLength, contract.GetProperty("groupLength").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachTypedCodeIsReadAsTheContractSays(int index)
    {
        var item = Contract().GetProperty("cases")[index];
        var typed = item.GetProperty("typed").GetString();

        var normalised = InviteCodes.Normalise(typed);

        Assert.Equal(item.GetProperty("normalised").GetString(), normalised);
        Assert.Equal(item.GetProperty("wellFormed").GetBoolean(), InviteCodes.IsComplete(normalised));
        Assert.Equal(item.GetProperty("formatted").GetString(), InviteCodes.Format(typed));
    }

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

    private static JsonElement Contract()
    {
        using var stream = typeof(InviteCodesTests).Assembly.GetManifestResourceStream("ScreenTail.Tests.invite-code.v1.json")
            ?? throw new InvalidOperationException("shared/contracts/invite-code.v1.json is not embedded in the test assembly");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
