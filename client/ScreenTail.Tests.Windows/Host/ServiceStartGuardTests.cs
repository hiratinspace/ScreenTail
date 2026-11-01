using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Service.Host;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// The capture service's start check read from a real token and a real session (ST-112, INV-6). CI's hosted
/// runner runs elevated, so this is where the elevation read is held to what Windows says about the same
/// token by other means. No desktop is needed.
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
        Assert.Equal(identity.User?.Value, facts.TokenUser);
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

        Measurements.Record($"Start guard on this machine: elevation {facts.Elevation}, session {(facts.SessionId == 0 ? "0" : "interactive")}, session user is the token user: {string.Equals(facts.TokenUser, facts.SessionUser, StringComparison.OrdinalIgnoreCase)}, decision {StartGuard.Decide(facts)}");
    }

    [Fact]
    public void ATestRunInTheSignedInUsersSessionIsFitUnlessItsTokenIsElevated()
    {
        // The hosted runner and the laptop both run tests as the user signed in to the session. With UAC off
        // the token is Default and the service runs; an elevated split token is the one thing refused.
        var facts = ServiceStart.Read();
        if (facts.SessionId == 0)
        {
            Assert.Equal(StartRefusal.SessionZero, StartGuard.Decide(facts));
            return;
        }

        Assert.Equal(facts.TokenUser, facts.SessionUser, ignoreCase: true);
        Assert.Equal(facts.Elevation == TokenElevation.Full ? StartRefusal.Elevated : StartRefusal.None, StartGuard.Decide(facts));
    }

    [Fact]
    public void AnElevatedTokenExitsWithZeroAndAFixedSentenceAndAFitOneGoesOn()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;

        using var refused = new StringWriter();
        Assert.Equal(0, ServiceStart.RefuseIfUnfit(new StartFacts(TokenElevation.Full, 2, sid, sid), refused));
        Assert.Equal(StartGuard.Explain(StartRefusal.Elevated), refused.ToString().Trim());
        Assert.DoesNotContain(sid, refused.ToString(), StringComparison.OrdinalIgnoreCase);

        using var fit = new StringWriter();
        Assert.Null(ServiceStart.RefuseIfUnfit(new StartFacts(TokenElevation.Default, 2, sid, sid), fit));
        Assert.Null(ServiceStart.RefuseIfUnfit(new StartFacts(TokenElevation.Limited, 2, sid, sid), fit));
        Assert.Empty(fit.ToString());
    }
}
