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
        Assert.Equal(StartRefusal.None, StartGuard.Decide(new StartFacts(elevation, 2, UserMatch.Same)));
    }

    [Fact]
    public void AnElevatedTokenIsRefused()
    {
        // Run as administrator, or restarted by Restart Manager from an elevated installer.
        Assert.Equal(StartRefusal.Elevated, StartGuard.Decide(new StartFacts(TokenElevation.Full, 2, UserMatch.Same)));
    }

    [Fact]
    public void AnElevationThatCannotBeReadIsRefused()
    {
        Assert.Equal(StartRefusal.Elevated, StartGuard.Decide(new StartFacts(TokenElevation.Unknown, 2, UserMatch.Same)));
    }

    [Fact]
    public void SessionZeroIsRefusedWhateverTheToken()
    {
        Assert.Equal(StartRefusal.SessionZero, StartGuard.Decide(new StartFacts(TokenElevation.Default, 0, UserMatch.Same)));
        Assert.Equal(StartRefusal.SessionZero, StartGuard.Decide(new StartFacts(TokenElevation.Full, 0, UserMatch.Different)));
    }

    [Fact]
    public void AnotherUserIsRefused()
    {
        Assert.Equal(StartRefusal.NotTheSessionsUser, StartGuard.Decide(new StartFacts(TokenElevation.Limited, 2, UserMatch.Different)));
    }

    [Fact]
    public void AUserThatCannotBeComparedRunsRatherThanBeRefusedAtEverySignIn()
    {
        // Only a definite mismatch refuses: a check that cannot read the session must not keep the service
        // from ever starting.
        Assert.Equal(StartRefusal.None, StartGuard.Decide(new StartFacts(TokenElevation.Limited, 2, UserMatch.Unknown)));
    }

    // ---- the comparison itself (SessionUser) --------------------------------------------------------------

    [Fact]
    public void TheShellsSidDecidesWhenItIsThere()
    {
        Assert.Equal(UserMatch.Same, SessionUser.Compare(Technician, Technician.ToLowerInvariant(), null, null));
        Assert.Equal(UserMatch.Different, SessionUser.Compare(Installer, Technician, new AccountName("tech", "PC"), new AccountName("tech", "PC")));
    }

    [Fact]
    public void WithoutAShellTheLogonSessionsNamesAreComparedAsText()
    {
        Assert.Equal(UserMatch.Same, SessionUser.Compare(Technician, null, new AccountName("Sam", "AzureAD"), new AccountName("sam", "AZUREAD")));
        Assert.Equal(UserMatch.Different, SessionUser.Compare(Technician, null, new AccountName("admin", "PC"), new AccountName("sam", "PC")));
        Assert.Equal(UserMatch.Different, SessionUser.Compare(Technician, null, new AccountName("sam", "OTHERDOMAIN"), new AccountName("sam", "PC")));
    }

    [Fact]
    public void NothingToCompareIsUnknown()
    {
        Assert.Equal(UserMatch.Unknown, SessionUser.Compare(Technician, null, null, new AccountName("sam", "PC")));
        Assert.Equal(UserMatch.Unknown, SessionUser.Compare(Technician, null, new AccountName("sam", "PC"), null));
        Assert.Equal(UserMatch.Unknown, SessionUser.Compare(null, Technician, null, null));
        Assert.Equal(UserMatch.Unknown, SessionUser.Compare(Technician, string.Empty, new AccountName(string.Empty, "PC"), new AccountName("sam", "PC")));
    }

    [Fact]
    public void AShellThatIsNotUpYetIsWaitedForBriefly()
    {
        // Explorer restarting at sign-in: no shell for the first two looks, then there is one.
        var looks = 0;
        var waits = 0;
        var match = SessionUser.Resolve(
            Technician,
            () => ++looks < 3 ? null : Technician,
            () => throw new InvalidOperationException("the names are not needed when the shell answers"),
            () => throw new InvalidOperationException("the names are not needed when the shell answers"),
            wait: () => waits++);

        Assert.Equal(UserMatch.Same, match);
        Assert.Equal(3, looks);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void NoShellAtAllFallsBackToTheNamesAfterABoundedWait()
    {
        var looks = 0;
        var waits = 0;
        var match = SessionUser.Resolve(Technician, () => { looks++; return null; }, () => new AccountName("sam", "PC"), () => new AccountName("admin", "PC"), wait: () => waits++);

        Assert.Equal(UserMatch.Different, match);
        Assert.Equal(SessionUser.ShellAttempts, looks);
        Assert.Equal(SessionUser.ShellAttempts - 1, waits);
        Assert.True(SessionUser.ShellAttempts * SessionUser.ShellRetry <= TimeSpan.FromSeconds(5), "the wait at start is not short");

        Assert.Equal(UserMatch.Unknown, SessionUser.Resolve(Technician, () => null, () => null, () => null, wait: () => { }));
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
        var ui = StartGuard.ExplainForUi(refusal);
        Assert.EndsWith("Exiting.", ui, StringComparison.Ordinal);
        Assert.NotEqual(said, ui);
    }

    [Fact]
    public void EachRefusalHasItsOwnEventIdForEachProgram()
    {
        var ids = new[] { StartRefusal.SessionZero, StartRefusal.Elevated, StartRefusal.NotTheSessionsUser }
            .SelectMany(r => new[] { StartGuard.EventId(r, ui: false), StartGuard.EventId(r, ui: true) })
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.InRange(id, 1100, 1299));
    }
}
