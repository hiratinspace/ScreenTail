using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Security.Principal;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Platform.Lifecycle;
using ScreenTail.Service.Host;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// The start check read from a real token and a real session (ST-112, INV-6), the way both processes read
/// it. CI's hosted runner runs with UAC off and the laptop with it on, so between them the Default and the
/// Limited reads are both held to what Windows says by other means, and on the laptop the elevated half of
/// the split token is read for real. No desktop is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceStartGuardTests
{
    [Fact]
    public void TheFactsAreThisProcesssAsWindowsReportsThem()
    {
        var facts = ServiceStart.Read();
        using var identity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();

        Assert.Equal((uint)self.SessionId, facts.SessionId);
        Assert.NotEqual(TokenElevation.Unknown, facts.Elevation);

        // The elevation type against the token's groups: an elevated split token is an administrator's, and
        // the limited half of one is not in the Administrators role.
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (facts.Elevation == TokenElevation.Full)
        {
            Assert.True(administrator, "an elevated token outside the Administrators role");
        }

        if (facts.Elevation == TokenElevation.Limited)
        {
            Assert.False(administrator, "a limited token in the Administrators role");
        }

        // Each source on its own: the shell, when there is one, is this user's; the logon session's names and
        // the session's are both there and agree, read without a name lookup.
        var shell = StartFactsReader.ShellUserSid();
        if (shell is not null)
        {
            Assert.Equal(identity.User?.Value, shell, ignoreCase: true);
        }

        var logon = StartFactsReader.LogonSessionName(identity.Token);
        var signedIn = StartFactsReader.SignedInName(facts.SessionId);
        Assert.NotNull(logon);
        Assert.NotNull(signedIn);
        Assert.Equal(UserMatch.Same, SessionUser.Compare(null, null, logon, signedIn));

        Measurements.Record($"Start guard on this machine: elevation {facts.Elevation}, session {(facts.SessionId == 0 ? "0" : "interactive")}, shell {(shell is null ? "not readable" : "read")}, user {facts.User}, decision {StartGuard.Decide(facts)}");
    }

    [Fact]
    public void ATestRunInTheSignedInUsersSessionIsTheSameUserAndFitUnlessItsTokenIsElevated()
    {
        var facts = ServiceStart.Read();
        if (facts.SessionId == 0)
        {
            Assert.Equal(StartRefusal.SessionZero, StartGuard.Decide(facts));
            return;
        }

        Assert.Equal(UserMatch.Same, facts.User);
        Assert.Equal(facts.Elevation == TokenElevation.Full ? StartRefusal.Elevated : StartRefusal.None, StartGuard.Decide(facts));
    }

    [Fact]
    public void TheElevatedHalfOfASplitTokenReadsFullAndIsRefused()
    {
        // With UAC on (the laptop) this process's token is the limited half, and its linked token is the
        // elevated one "Run as administrator" would have given. With UAC off (the hosted runner) there is no
        // pair, and the read says Default; that is the case the Default branch covers.
        using var identity = WindowsIdentity.GetCurrent();
        var own = StartFactsReader.ElevationOf(identity.Token);
        var linked = StartFactsReader.LinkedToken(identity.Token);
        try
        {
            if (own == TokenElevation.Limited)
            {
                Assert.NotEqual(IntPtr.Zero, linked);
                var elevation = StartFactsReader.ElevationOf(linked);
                Assert.Equal(TokenElevation.Full, elevation);
                Assert.Equal(StartRefusal.Elevated, StartGuard.Decide(new StartFacts(elevation, 2, UserMatch.Same)));
            }
            else
            {
                Assert.Contains(own, new[] { TokenElevation.Default, TokenElevation.Full });
            }
        }
        finally
        {
            StartFactsReader.Close(linked);
        }
    }

    [Fact]
    public void AnElevatedTokenExitsWithZeroAndIsSaidAndRecordedAndAFitOneGoesOn()
    {
        var recorded = new List<StartRefusal>();
        using var refused = new StringWriter();
        Assert.Equal(0, ServiceStart.RefuseIfUnfit(new StartFacts(TokenElevation.Full, 2, UserMatch.Same), refused, r => { recorded.Add(r); return true; }));
        Assert.Equal(StartGuard.Explain(StartRefusal.Elevated), refused.ToString().Trim());
        Assert.Equal([StartRefusal.Elevated], recorded);

        using var fit = new StringWriter();
        Assert.Null(ServiceStart.RefuseIfUnfit(new StartFacts(TokenElevation.Default, 2, UserMatch.Same), fit, r => { recorded.Add(r); return true; }));
        Assert.Null(ServiceStart.RefuseIfUnfit(new StartFacts(TokenElevation.Limited, 2, UserMatch.Unknown), fit, r => { recorded.Add(r); return true; }));
        Assert.Empty(fit.ToString());
        Assert.Single(recorded);
    }

    [Fact]
    public void ARefusalIsWrittenToTheApplicationLogWithoutAdministratorRightsOrARegisteredSource()
    {
        // What an installed build leaves behind when it refuses: its standard error goes nowhere.
        var before = DateTime.UtcNow.AddSeconds(-5);
        Assert.True(RefusalEventLog.Write(StartRefusal.NotTheSessionsUser, ui: true));

        var id = StartGuard.EventId(StartRefusal.NotTheSessionsUser, ui: true);
        var query = new EventLogQuery("Application", PathType.LogName, $"*[System[Provider[@Name='{RefusalEventLog.Source}'] and (EventID={id})]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        using var entry = reader.ReadEvent();

        Assert.NotNull(entry);
        Assert.True(entry.TimeCreated?.ToUniversalTime() >= before, "the entry found is an old one");
        Assert.Equal(StartGuard.ExplainForUi(StartRefusal.NotTheSessionsUser), Assert.Single(entry.Properties).Value as string);
    }
}
