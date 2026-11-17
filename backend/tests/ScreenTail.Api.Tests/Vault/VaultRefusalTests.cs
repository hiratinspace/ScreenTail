using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// The owner's reading of a technician's sentence (2026-11-15 security review, ST-082): a technician is
/// asked to ask the ScreenTail owner, in the web admin; the owner, on the provider's card there, is told
/// what to do and with which button.
/// </summary>
public sealed class VaultRefusalTests
{
    [Fact]
    public void TheTechnicianIsAskedToAskTheScreenTailOwnerInTheWebAdmin()
    {
        Assert.Equal("Ask your ScreenTail owner to connect it again in the web admin.", VaultRefusal.Todo);
        Assert.DoesNotContain("Settings", VaultRefusal.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "ConnectWise rejected the API key. Ask your ScreenTail owner to update the ConnectWise connection in the web admin.",
        "ConnectWise rejected the API key. Update the ConnectWise connection with Replace on this card.")]
    [InlineData(
        "Hudu rejected the API key. Ask your ScreenTail owner to update the Hudu connection in the web admin.",
        "Hudu rejected the API key. Update the Hudu connection with Replace on this card.")]
    [InlineData(
        "The stored credential could not be opened, so it was not used. Ask your ScreenTail owner to connect it again in the web admin.",
        "The stored credential could not be opened, so it was not used. Connect it again with Replace on this card.")]
    [InlineData(
        "The Hudu site answered with a redirect, and ScreenTail does not follow redirects. Ask your ScreenTail owner to check the site address in the web admin.",
        "The Hudu site answered with a redirect, and ScreenTail does not follow redirects. Check the site address with Replace on this card.")]
    [InlineData(
        "Hudu listed more than 5000 companies, which is more than ScreenTail reads. Ask your ScreenTail owner to check the Hudu site address in the web admin, or raise Hudu:MaxCompanies on the backend.",
        "Hudu listed more than 5000 companies, which is more than ScreenTail reads. Check the Hudu site address with Replace on this card, or raise Hudu:MaxCompanies on the backend.")]
    public void TheOwnerIsToldWhereOnTheWebAdminToFixIt(string technician, string owner) =>
        Assert.Equal(owner, VaultRefusal.ForOwner(technician));

    [Theory]
    [InlineData(
        "The ConnectWise site answered with something that is not its API. Ask your owner to check the site address in the web admin.",
        "The ConnectWise site answered with something that is not its API. Check the site address with Replace on this card.")]
    [InlineData(
        "Hudu rejected the API key. Update it in Settings → Integrations.",
        "Hudu rejected the API key. Update it with Replace on this card.")]
    public void ARowStoredInEarlierWordsReadsTheSameWay(string stored, string owner) =>
        Assert.Equal(owner, VaultRefusal.ForOwner(stored));

    [Theory]
    [InlineData("Connected to https://na.myconnectwise.net.")]
    [InlineData("Hudu did not answer. Try again in a minute.")]
    [InlineData("Ask your ScreenTail owner to ")]
    public void AnythingElseIsLeftAsItIs(string message) =>
        Assert.Equal(message, VaultRefusal.ForOwner(message));

    [Fact]
    public void NothingStaysNothing() => Assert.Null(VaultRefusal.ForOwner(null));
}
