using ScreenTail.Core.Install;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Tests.Lifecycle;

/// <summary>
/// The capture service registers with Windows for a restart after another program's installer closed it
/// (ST-112): only in the service itself, never in a one-shot mode, only for Restart Manager's restart, and
/// with a command line that is the ordinary start, which watches and does not record.
/// </summary>
public sealed class ApplicationRestartTests
{
    [Fact]
    public void OnlyRestartManagersRestartAndNotAfterACrashAHangOrAReboot()
    {
        // RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT. Not RESTART_NO_PATCH, which is the one
        // restart this exists for.
        Assert.Equal(0x1 | 0x2 | 0x8, ApplicationRestart.Flags);
    }

    [Fact]
    public void TheRestartIsTheOrdinaryStartWithNoArguments()
    {
        // The Run key starts the service with no arguments, and so does this: nothing that resumes a session
        // or starts one. A session the close interrupted is recovered as partial at that start
        // (CloseLeavesSessionForRecoveryTests).
        Assert.Empty(ApplicationRestart.CommandLine);
        Assert.True(ApplicationRestart.ShouldRegister([]));
    }

    [Theory]
    [InlineData(InstalledFootprint.UninstallFlag)]
    [InlineData("--upgrade-release")]
    [InlineData("--capabilities")]
    [InlineData("--transcribe")]
    public void NoOneShotModeRegisters(string mode)
    {
        Assert.False(ApplicationRestart.ShouldRegister([mode]));
        Assert.False(ApplicationRestart.ShouldRegister(["--model", "base.en", mode]));
    }

    [Fact]
    public void TheServiceRegistersOnceAfterEveryOneShotModeHasReturnedAndBeforeTheHostRuns()
    {
        // Program.cs dispatches the one-shot modes first and each returns; only the service gets as far as
        // the single-instance name, and only the copy that holds it registers.
        var program = File.ReadAllText(Repo.File("client", "ScreenTail.Service", "Program.cs"));

        var register = program.IndexOf("WindowsApplicationRestart.Register(", StringComparison.Ordinal);
        Assert.True(register > 0, "the service does not register for restart");
        Assert.Equal(register, program.LastIndexOf("WindowsApplicationRestart.Register(", StringComparison.Ordinal));
        var guard = program.LastIndexOf("ApplicationRestart.ShouldRegister(args)", register, StringComparison.Ordinal);
        Assert.True(guard > 0 && register - guard < 200, "the registration is not guarded by ShouldRegister");

        foreach (var earlier in new[] { "UninstallCleanup.Run(", "Transcribe.RunAsync(", "--capabilities", "SingleInstance.TryAcquire", "Another ScreenTail capture service is already running" })
        {
            var at = program.LastIndexOf(earlier, StringComparison.Ordinal);
            Assert.True(at > 0 && at < register, $"'{earlier}' comes after the registration");
        }

        Assert.True(register < program.IndexOf("ServiceRun.RunAsync(", StringComparison.Ordinal), "the host runs before the service registers");
    }

    /// <summary>A file in the repository, found from the test binary the way InstallerAuthoringTests finds one.</summary>
    private static class Repo
    {
        public static string File(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine([directory.FullName, .. parts]);
                if (System.IO.File.Exists(candidate) && Directory.Exists(Path.Combine(directory.FullName, "client")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"{string.Join('/', parts)} was not found above {AppContext.BaseDirectory}; these tests run from a checkout.");
        }
    }
}
