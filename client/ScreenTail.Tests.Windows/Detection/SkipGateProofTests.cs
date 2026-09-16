using System.Runtime.Versioning;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// TEMPORARY, and removed in the next commit on this branch.
///
/// ST-018's last acceptance criterion: a pull request that adds a skipped test fails CI, and the run log
/// names the test. This is that pull request. The gate should report one skip over budget on all three
/// jobs and name this class.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SkipGateProofTests
{
    [Fact]
    public void ThisTestSkipsOnPurposeToProveTheGateFires()
    {
        Assert.Skip("ST-018 proof: deliberately skipped to show the gate fails the build and names the test.");
    }
}
