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

    [Fact]
    public void EveryStartIsCheckedAfterTheNameIsTakenAndBeforeTheHostIsBuilt()
    {
        // StartGuard (INV-6): an elevated or foreign process never gets as far as building the host.
        var program = File.ReadAllText(Repo.File("client", "ScreenTail.Service", "Program.cs"));

        var guard = program.IndexOf("ServiceStart.RefuseIfUnfit(ServiceStart.Read()", StringComparison.Ordinal);
        Assert.True(guard > 0, "the service does not check itself at start");
        Assert.Equal(guard, program.LastIndexOf("ServiceStart.RefuseIfUnfit(", StringComparison.Ordinal));

        // Before the name, so a refused elevated copy never holds it; after the one-shot modes, which run as
        // the system account in session 0 for the installer and must not be refused.
        Assert.True(guard < program.IndexOf("SingleInstance.TryAcquire", StringComparison.Ordinal), "the single-instance name is taken before the check");
        foreach (var oneShot in new[] { "UninstallCleanup.Run(", "Transcribe.RunAsync(" })
        {
            Assert.True(program.IndexOf(oneShot, StringComparison.Ordinal) < guard, $"'{oneShot}' comes after the check");
        }

        Assert.True(guard < program.IndexOf("Host.CreateApplicationBuilder", StringComparison.Ordinal), "the host is built before the check");
        Assert.True(guard < program.IndexOf("SessionEndWindow.Start(", StringComparison.Ordinal), "a window is made before the check");
        Assert.True(guard < program.IndexOf("WindowsApplicationRestart.Register(", StringComparison.Ordinal), "a refused copy registers for restart");
    }

    [Fact]
    public void TheUiRegistersOnceAndOnlyWhenItIsTheApplication()
    {
        // The screenshot harnesses (--gallery, --hud, --note, --screenshot) return before the live shell is
        // made; the registration sits with it, after every one of them.
        var app = File.ReadAllText(Repo.File("client", "ScreenTail.UI", "App.xaml.cs"));

        var register = app.IndexOf("RestartRegistration.Register()", StringComparison.Ordinal);
        Assert.True(register > 0, "the UI does not register for restart");
        Assert.Equal(register, app.LastIndexOf("RestartRegistration.Register()", StringComparison.Ordinal));
        foreach (var harness in new[] { "new GalleryWindow(", "HudPreview.CaptureAsync(", "new Review.NotePreviewWindow(", "new ShellWindow(directory)" })
        {
            var at = app.IndexOf(harness, StringComparison.Ordinal);
            Assert.True(at > 0 && at < register, $"'{harness}' comes after the registration");
        }

        var live = app.IndexOf("_live = new Shell.LiveShell();", StringComparison.Ordinal);
        Assert.True(live > register && live - register < 200, "the registration is not with the live shell's start");
    }

    [Fact]
    public void TheUiChecksItselfBeforeItRegistersOrConnects()
    {
        // The same StartGuard as the service: an elevated or foreign UI exits quietly, with an Application log
        // entry, before it registers for restart or makes the live shell.
        var app = File.ReadAllText(Repo.File("client", "ScreenTail.UI", "App.xaml.cs"));

        var guard = app.IndexOf("StartGuard.Decide(Platform.Lifecycle.StartFactsReader.Read())", StringComparison.Ordinal);
        Assert.True(guard > 0, "the UI does not check itself at start");
        Assert.Equal(guard, app.LastIndexOf("StartGuard.Decide(", StringComparison.Ordinal));
        Assert.True(guard < app.IndexOf("RestartRegistration.Register()", StringComparison.Ordinal), "the UI registers before the check");
        Assert.True(guard < app.IndexOf("_live = new Shell.LiveShell();", StringComparison.Ordinal), "the live shell is made before the check");

        var refused = app.IndexOf("if (unfit != Core.Lifecycle.StartRefusal.None)", guard, StringComparison.Ordinal);
        var exits = app.IndexOf("Shutdown(0);", guard, StringComparison.Ordinal);
        Assert.True(refused > guard && exits > refused && exits < app.IndexOf("RestartRegistration.Register()", StringComparison.Ordinal), "a refused UI does not exit before it registers");
        Assert.True(app.IndexOf("RefusalEventLog.Write(unfit, ui: true)", guard, StringComparison.Ordinal) is var logged && logged > refused && logged < exits, "a refused UI is not recorded");
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
