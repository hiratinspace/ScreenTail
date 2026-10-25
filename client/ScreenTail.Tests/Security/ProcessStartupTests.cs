using System.Text.RegularExpressions;

namespace ScreenTail.Tests.Security;

/// <summary>
/// What the two processes do before any of their own code runs, and how the service logs (ST-011,
/// ST-012; 2026-10-12 review, S-33 and T-115).
///
/// Both are read from the files rather than from a build, because neither project can be loaded here:
/// the service and the UI are Windows-only, and a top-level <c>Program.cs</c> has no seam a test could
/// call. What is pinned is the text that the behaviour turns on, which is also what a hurried change
/// would edit.
/// </summary>
public sealed class ProcessStartupTests
{
    [Fact]
    public void TheServiceLogsOnlyThroughTheScrubber()
    {
        // INV-10. The host builder adds its default sinks (console, debug, and on Windows the Event
        // Log) unless they are cleared, and every one of them would carry unscrubbed lines. Dropping
        // the ClearProviders call would send the raw text to the Event Log without any test noticing.
        var program = File.ReadAllText(Source("ScreenTail.Service", "Program.cs"));

        Assert.Contains("Logging.ClearProviders()", program, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(program, @"\.AddProvider\("));
        Assert.Contains(".AddProvider(new ScrubbingLoggerProvider(", program, StringComparison.Ordinal);
        foreach (var sink in (string[])["AddConsole", "AddSimpleConsole", "AddJsonConsole", "AddDebug", "AddEventLog", "AddEventSourceLogger"])
        {
            Assert.DoesNotContain(sink, program, StringComparison.Ordinal);
        }

        Assert.True(
            program.IndexOf("Logging.ClearProviders()", StringComparison.Ordinal) < program.IndexOf(".AddProvider(", StringComparison.Ordinal),
            "the providers are cleared before the scrubber is added, not after");
    }

    [Theory]
    [InlineData("ScreenTail.Service")]
    [InlineData("ScreenTail.UI")]
    public void BothProcessesRefuseStartupHooksBeforeAnyCodeRuns(string project)
    {
        // S-33. RunningHonestly checks DOTNET_STARTUP_HOOKS at the top of Main, and a startup hook runs
        // before Main: by the time the check ran, the other code had already run. The runtime has a
        // switch that stops it loading hooks at all, and it is set at build time, so there is nothing
        // for an environment variable to ask for.
        var csproj = File.ReadAllText(Source(project, project + ".csproj"));

        Assert.Matches(new Regex(@"<StartupHookSupport>\s*false\s*</StartupHookSupport>"), csproj);
    }

    /// <summary>A file in the client's source tree, found from the test binary the way ClientSources finds projects.</summary>
    private static string Source(string project, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "client", project, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"client/{project}/{file} was not found above {AppContext.BaseDirectory}; these tests run from a checkout.");
    }
}
