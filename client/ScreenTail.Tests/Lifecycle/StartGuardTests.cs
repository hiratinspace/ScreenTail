using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Tests.Lifecycle;

/// <summary>
/// The capture service refuses to run elevated, as another user, or in session 0 (ST-112, INV-6): a service
/// at High integrity no longer tells an administrator's elevated windows apart, and Restart Manager restarts
/// a program with the token of the installer that asked.
/// </summary>
public sealed class StartGuardTests
{
    private const string Technician = "S-1-5-21-1000-2000-3000-1001";
    private const string Installer = "S-1-5-21-1000-2000-3000-500";

    [Theory]
    [InlineData(TokenElevation.Limited)]
    [InlineData(TokenElevation.Default)]
    public void TheTechniciansOwnTokenInTheirSessionRuns(TokenElevation elevation)
    {
        // Limited is the Run key's start for an administrator with UAC on; Default a standard user, or UAC
        // off, where ProcessElevation already judges windows against the service's own level.
        Assert.Equal(StartRefusal.None, StartGuard.Decide(new StartFacts(elevation, 2, Technician, Technician)));
    }

    [Fact]
    public void AnElevatedTokenIsRefused()
    {
        // Run as administrator, or restarted by Restart Manager from an elevated installer.
        Assert.Equal(StartRefusal.Elevated, StartGuard.Decide(new StartFacts(TokenElevation.Full, 2, Technician, Technician)));
    }

    [Fact]
    public void AnElevationThatCannotBeReadIsRefused()
    {
        Assert.Equal(StartRefusal.Elevated, StartGuard.Decide(new StartFacts(TokenElevation.Unknown, 2, Technician, Technician)));
    }

    [Fact]
    public void SessionZeroIsRefusedWhateverTheToken()
    {
        Assert.Equal(StartRefusal.SessionZero, StartGuard.Decide(new StartFacts(TokenElevation.Default, 0, Technician, Technician)));
        Assert.Equal(StartRefusal.SessionZero, StartGuard.Decide(new StartFacts(TokenElevation.Full, 0, Installer, Technician)));
    }

    [Theory]
    [InlineData(Installer, Technician)]
    [InlineData(null, Technician)]
    [InlineData(Technician, null)]
    [InlineData("", Technician)]
    [InlineData(Technician, "")]
    public void AnotherUserOrAUserThatCannotBeReadIsRefused(string? tokenUser, string? sessionUser)
    {
        Assert.Equal(StartRefusal.NotTheSessionsUser, StartGuard.Decide(new StartFacts(TokenElevation.Limited, 2, tokenUser, sessionUser)));
    }

    [Fact]
    public void SidsCompareWithoutCase()
    {
        Assert.Equal(StartRefusal.None, StartGuard.Decide(new StartFacts(TokenElevation.Limited, 2, Technician, Technician.ToLowerInvariant())));
    }

    [Theory]
    [InlineData(StartRefusal.SessionZero)]
    [InlineData(StartRefusal.Elevated)]
    [InlineData(StartRefusal.NotTheSessionsUser)]
    public void EveryRefusalIsSaidInAFixedSentence(StartRefusal refusal)
    {
        var said = StartGuard.Explain(refusal);
        Assert.EndsWith("Exiting.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-", said, StringComparison.Ordinal);
        Assert.Empty(StartGuard.Explain(StartRefusal.None));
    }
}
