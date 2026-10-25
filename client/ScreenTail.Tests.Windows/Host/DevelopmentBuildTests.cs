using System.Runtime.Versioning;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// What makes a build a development build, which is what lets both processes start beside a startup
/// hook and accept an unsigned sibling (ST-012; 2026-10-12 review, S-21).
///
/// It used to be "no signature verifies", so a shipped build whose signature stopped verifying (an
/// expired certificate under lifetime signing, a chain the machine cannot build) quietly became a
/// development build and applied the permissive rules. It is now "nothing signed this": a file Windows
/// says carries no signature at all. Anything else, including a file whose path cannot be found, is
/// held to the shipped rules. No desktop is needed, so this runs on the hosted runner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DevelopmentBuildTests
{
    [Fact]
    public void ADevelopmentBuildIsOneNothingSignedAndNothingElse()
    {
        var dotnet = Path.Combine(
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "dotnet.exe");

        // Unknown is not unsigned: a process that cannot say what it started from is held to the strict rule.
        Assert.False(Signing.IsDevelopment(null));

        // This test assembly is a build nobody signed.
        Assert.True(Signing.IsDevelopment(typeof(DevelopmentBuildTests).Assembly.Location));

        // A file Windows accepts the signature of is not a development build. Asserted only where the
        // runtime is installed in the usual place; the two cases above hold everywhere.
        if (File.Exists(dotnet))
        {
            Assert.False(Signing.IsDevelopment(dotnet));
        }
    }
}
