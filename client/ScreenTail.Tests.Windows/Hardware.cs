using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Capabilities;
using ScreenTail.Service.Capabilities;

namespace ScreenTail.Tests.Windows;

/// <summary>
/// The one place a Windows test decides it cannot run, and says so where a workflow can see it (ST-018).
///
/// Every test in this project is here because it needs something only a real machine has: a desktop
/// session, a screen to capture, an OCR language pack. When the machine does not have it, skipping is the
/// honest outcome — a foreground test on a machine with no foreground proves nothing, and passing it
/// vacuously would be worse than not running it.
///
/// What was not honest was the silence. The <c>NO-DESKTOP</c> marker exists so a run that verified
/// nothing fails instead of going green, but it was written by <see cref="DesktopWindow.RequireForeground"/>
/// — which a test only reaches after it has already constructed a window. The ten tests most at risk
/// asked the capability probe first and returned before building anything, so they skipped in silence and
/// the canary reported success (weaknesses P1-4). Asking through here writes the marker first.
///
/// Test counts are the other half. <c>dotnet test</c> exits 0 when every test skips, so the skip count is
/// checked against a committed baseline in <c>skip-baseline.json</c>; see
/// <c>scripts/windows/check-skips.ps1</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Hardware
{
    /// <summary>Whether a timing measured on this machine is worth enforcing a budget against.</summary>
    public static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);

    /// <summary>Skips unless this machine has an interactive desktop, recording why when it does not.</summary>
    public static void RequireDesktop([CallerMemberName] string test = "") =>
        Require(Capability.DesktopSession, "no interactive desktop", test);

    /// <summary>Skips unless this machine can take a screenshot of a real window.</summary>
    public static void RequireScreenCapture([CallerMemberName] string test = "")
    {
        Require(Capability.DesktopSession, "no interactive desktop", test);
        Require(Capability.ScreenCapture, "screen capture is blocked", test);
    }

    private static void Require(Capability capability, string why, string test)
    {
        var check = new WindowsCapabilityProbe().Probe()[capability];
        if (check.State == CapabilityState.Ok)
        {
            return;
        }

        // Written before the skip, because a skip is where this stops being visible. The workflow greps
        // the measurements file for the marker and fails the job on it.
        Measurements.Record($"{DesktopWindow.NoDesktopMarker}: {test} skipped — {why} ({check.Message})");
        Assert.Skip($"{test}: {why}. {check.Message}");
    }
}
