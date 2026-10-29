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

    [Fact]
    public void TheServiceLogsAtTheLevelTheEnvironmentAsksFor()
    {
        // ST-011 AC3, found by ST-013's end-to-end run: it started the service with
        // SCREENTAIL_LOG_LEVEL=Debug and the log held no Debug line. The scrubber was given the level,
        // and the host's own filter, Information unless configured, dropped everything below it before
        // the scrubber saw it. The filter has to be set to the same level.
        var program = File.ReadAllText(Source("ScreenTail.Service", "Program.cs"));

        var parsed = Regex.Match(program, @"var (\w+) = LogLevels\.Parse\(Environment\.GetEnvironmentVariable\(LogLevels\.Variable\)\);");
        Assert.True(parsed.Success, "the level is read once, from SCREENTAIL_LOG_LEVEL");
        var level = parsed.Groups[1].Value;
        Assert.Contains($"Logging.SetMinimumLevel({level})", program, StringComparison.Ordinal);
        Assert.Contains($"new ScrubbingLoggerProvider(Console.Error, {level})", program, StringComparison.Ordinal);
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

    [Fact]
    public void ASignatureIsNotAskedToExpireWithItsCertificate()
    {
        // S-21. Lifetime signing makes WinVerifyTrust treat a signature as invalid once its certificate
        // expires, timestamp or not. On that date both peer checks would refuse every process, and a
        // shipped build would start calling itself a development build. Only Windows can run the call,
        // so the flag is pinned here where every platform's suite reads it, and the development rule is
        // pinned to "nothing signed this" (DevelopmentBuildTests runs that half on Windows).
        var authenticode = File.ReadAllText(Source("ScreenTail.Platform", Path.Combine("Ipc", "Authenticode.cs")));
        var signing = File.ReadAllText(Source("ScreenTail.Platform", Path.Combine("Ipc", "Signing.cs")));

        Assert.DoesNotContain("WTD_LIFETIME_SIGNING_FLAG", authenticode, StringComparison.Ordinal);

        // By value as well as by name: the flag is 0x800, and a literal would ask for it just the same.
        // Every provider flag set is the one this file means to set, and nothing else.
        var flags = authenticode.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("ProvFlags =", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(flags);
        Assert.All(flags, line => Assert.Equal("ProvFlags = WTD_SAFER_FLAG,", line));

        Assert.Contains("Authenticode.IsUnsigned(", signing, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifiedSignerThumbprint", signing, StringComparison.Ordinal);
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
