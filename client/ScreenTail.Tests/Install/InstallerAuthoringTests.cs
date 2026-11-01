using System.Text.RegularExpressions;
using System.Xml.Linq;
using ScreenTail.Core.Install;

namespace ScreenTail.Tests.Install;

/// <summary>
/// ST-112: the installer's authoring and the publish settings it packages, read from the files (ADR-0007).
///
/// The installer can only be built and run on Windows, and CI's installer job is where it is. What can be
/// held on every machine is the text the behaviour turns on, which is also what a hurried change would
/// edit: a per-user scope, an uninstall that also runs during an upgrade and takes everybody's sessions
/// with it, a self-extracting build that unpacks native code into a folder the user can write to.
/// </summary>
public sealed class InstallerAuthoringTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";
    private static readonly XNamespace MsBuild = string.Empty;

    [Fact]
    public void ScreenTailsOwnInstallerNeverRestartsWhatRestartManagerClosed()
    {
        // The service registers for restart for other programs' installers (ApplicationRestart). This
        // package turns Windows Installer's restart off, so its upgrade and uninstall leave the service down
        // until the next sign-in, and nothing is started from the installer's elevated process (ST-112).
        var property = Assert.Single(Package().Descendants(Wix + "Property"), p => (string?)p.Attribute("Id") == "MSIDISABLERMRESTART");
        Assert.Equal("1", (string?)property.Attribute("Value"));
    }

    [Fact]
    public void ScreenTailsOwnInstallerAsksOnlyProgramsRegisteredForRestartToClose()
    {
        // MSIRMSHUTDOWN=2: the service, registered for restart, is asked; a scanner or an agent that happens
        // to hold an installed file is never asked to close, and if one holds it nobody is (ST-112).
        var property = Assert.Single(Package().Descendants(Wix + "Property"), p => (string?)p.Attribute("Id") == "MSIRMSHUTDOWN");
        Assert.Equal("2", (string?)property.Attribute("Value"));
    }

    [Fact]
    public void TheInstallIsPerMachine()
    {
        // MSP tools (Intune, RMM agents) install as the system account, so a per-user package would land
        // in the system profile and nobody would ever run it. And Program Files is writable by
        // administrators only: the files the peer check vouches for are not files the user can swap.
        var package = Package().Root!.Element(Wix + "Package")!;

        Assert.Equal("perMachine", (string?)package.Attribute("Scope"));
        Assert.Equal("ProgramFiles64Folder", (string?)Package().Descendants(Wix + "StandardDirectory").First(d => d.Element(Wix + "Directory")?.Attribute("Id")?.Value == "INSTALLFOLDER").Attribute("Id"));
    }

    [Fact]
    public void TheUpgradeCodeNeverChanges()
    {
        // Windows recognises an older ScreenTail by this code and nothing else. A new one makes the next
        // version install beside the old one instead of replacing it, on every machine at once.
        var package = Package().Root!.Element(Wix + "Package")!;

        Assert.Equal("{85199A85-ACAA-43AA-8CDC-BA38D3570929}", (string?)package.Attribute("UpgradeCode"));
        Assert.NotNull(package.Element(Wix + "MajorUpgrade")?.Attribute("DowngradeErrorMessage"));
    }

    [Fact]
    public void TheServiceStartsAtSignInForEveryUser()
    {
        // ADR-0003: the service is a per-user process started from a Run key, inside the signed-in
        // session where hooks and capture work. A per-machine install writes the machine's Run key, so
        // it starts for whoever signs in. The value has the name the development helper uses, so the two
        // never start two copies under two names (the single-instance mutex would refuse the second).
        var run = Package().Descendants(Wix + "RegistryValue")
            .Where(v => (string?)v.Attribute("Key") == InstalledFootprint.RunKey)
            .ToList();

        var service = Assert.Single(run);
        Assert.Equal("HKLM", (string?)service.Attribute("Root"));
        Assert.Equal(InstalledFootprint.ServiceRunValue, (string?)service.Attribute("Name"));
        Assert.Equal($"\"[INSTALLFOLDER]{InstalledFootprint.ServiceExecutable}\"", (string?)service.Attribute("Value"));

        var loginStartup = File.ReadAllText(Repo.File("client", "ScreenTail.Service", "Host", "LoginStartup.cs"));
        Assert.Contains($"ServiceValueName = \"{InstalledFootprint.ServiceRunValue}\"", loginStartup, StringComparison.Ordinal);
    }

    [Fact]
    public void UserDataIsRemovedOnUninstallAndNeverDuringAnUpgrade()
    {
        // INV-12 and the ticket: an uninstall leaves nothing. A major upgrade uninstalls the old version
        // as one of its steps, and the same action running then would erase every technician's sessions
        // on every machine the new version reached. UPGRADINGPRODUCTCODE is what tells the two apart.
        var actionId = RemovalAction().Attribute("Id")!.Value;
        var scheduled = Package().Descendants(Wix + "Custom").Single(c => (string?)c.Attribute("Action") == actionId);
        var condition = (string)scheduled.Attribute("Condition")!;

        Assert.Equal("RemoveFiles", (string?)scheduled.Attribute("Before"));
        Assert.Contains("REMOVE~=\"ALL\"", condition, StringComparison.Ordinal);
        Assert.Contains("NOT UPGRADINGPRODUCTCODE", condition, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRemovalRunsOnceForTheMachineAsTheSystemAccount()
    {
        // Deferred and not impersonated: only the system account may hold a signed-in user's token, which
        // is what each user's folder is deleted with. It runs the installed service's own uninstall mode,
        // which refuses to run for anybody but an administrator, through WixQuietExec so what it reports
        // (how many folders it deleted and how many it left) is in the uninstall log.
        var action = RemovalAction();

        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("WixQuietExec", (string?)action.Attribute("DllEntry"));
        Assert.Equal(
            $"\"[INSTALLFOLDER]{InstalledFootprint.ServiceExecutable}\" {InstalledFootprint.UninstallFlag}",
            (string?)Package().Descendants(Wix + "SetProperty").Single(p => (string?)p.Attribute("Id") == (string?)action.Attribute("Id")).Attribute("Value"));

        // Ignored rather than checked: a data folder that cannot be removed should not leave the product
        // half uninstalled. The smoke test in CI is what notices one that was left.
        Assert.Equal("ignore", (string?)action.Attribute("Return"));
    }

    [Fact]
    public void RunningCopiesAreClosedBeforeTheirFilesAreReplacedOrRemoved()
    {
        // A running executable cannot be replaced or deleted. Without this an upgrade or an uninstall
        // pushed by an MSP while somebody is signed in finishes with "restart required" and old files.
        var closed = Package().Descendants(Util + "CloseApplication").ToList();

        Assert.Equal(
            [InstalledFootprint.ServiceExecutable, InstalledFootprint.UiExecutable],
            closed.Select(c => (string)c.Attribute("Target")!).Order(StringComparer.Ordinal));
        Assert.All(closed, c => Assert.NotNull(c.Attribute("TerminateProcess")));
        Assert.All(closed, c => Assert.Equal("no", (string?)c.Attribute("RebootPrompt")));

        // Only when files are about to be replaced or removed: an upgrade, or an uninstall. A first
        // install has nothing running, and a repair must not end a technician's recording.
        Assert.All(closed, c => Assert.Equal("WIX_UPGRADE_DETECTED OR REMOVE~=\"ALL\"", (string?)c.Attribute("Condition")));
    }

    [Fact]
    public void TheInstallFolderCannotBeMovedOutOfProgramFiles()
    {
        // INSTALLFOLDER is a public property, so a command line could install somewhere ordinary users
        // can write, and the uninstall would then run the service from there as the system account. The
        // package refuses any folder but ScreenTail under the machine's own Program Files, worked out
        // from the environment rather than from a property the same command line could set.
        var expected = Package().Descendants(Wix + "SetProperty").Single(p => (string?)p.Attribute("Id") == "EXPECTEDINSTALLFOLDER");
        Assert.Equal(@"[%ProgramW6432]\ScreenTail\", (string?)expected.Attribute("Value"));

        var refusal = Package().Descendants(Wix + "CustomAction").Single(a => (string?)a.Attribute("Id") == "RefuseInstallFolder");
        Assert.False(string.IsNullOrWhiteSpace((string?)refusal.Attribute("Error")));

        foreach (var sequence in new[] { "InstallUISequence", "InstallExecuteSequence" })
        {
            var scheduled = Package().Descendants(Wix + sequence).Elements(Wix + "Custom").Single(c => (string?)c.Attribute("Action") == "RefuseInstallFolder");
            Assert.Equal("CostFinalize", (string?)scheduled.Attribute("After"));
            Assert.Equal("NOT Installed AND INSTALLFOLDER ~<> EXPECTEDINSTALLFOLDER", (string?)scheduled.Attribute("Condition"));
        }
    }

    [Fact]
    public void TheWholePayloadLandsInOneFolder()
    {
        // ADR-0003's development rule: an unsigned peer is accepted only from the same directory. Every
        // build is unsigned until the certificate exists, so an installer that split the two executables
        // would install a service and a UI that refuse each other.
        var files = Assert.Single(Package().Descendants(Wix + "Files"));

        Assert.Equal(@"$(var.Payload)\**", (string?)files.Attribute("Include"));
        Assert.Equal("INSTALLFOLDER", (string?)files.Parent!.Attribute("Directory"));
    }

    [Fact]
    public void TheRuntimeCheckNeverBlocksAnUninstallOrARepair()
    {
        // Whisper's native libraries need the Visual C++ runtime, which a fresh Windows does not have.
        // The package refuses to install without it rather than install a service whose speech fails;
        // an installed product is never held hostage to it.
        var launch = Assert.Single(Package().Descendants(Wix + "Launch"));

        Assert.StartsWith("Installed OR ", (string)launch.Attribute("Condition")!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundleInstallsTheRuntimeFirstAndNeverRemovesIt()
    {
        // The setup program is for machines that do not have the runtime yet. The runtime is shared by
        // other software, so uninstalling ScreenTail must not take it away from them.
        var chain = Bundle().Descendants(Wix + "Chain").Single().Elements().ToList();

        Assert.Equal(["ExePackage", "MsiPackage"], chain.Select(p => p.Name.LocalName));
        Assert.Equal("yes", (string?)chain[0].Attribute("Permanent"));
        Assert.Equal("yes", (string?)chain[0].Attribute("PerMachine"));
        Assert.NotEqual(
            (string?)Package().Root!.Element(Wix + "Package")!.Attribute("UpgradeCode"),
            (string?)Bundle().Root!.Element(Wix + "Bundle")!.Attribute("UpgradeCode"));
    }

    [Theory]
    [InlineData("ScreenTail.Service")]
    [InlineData("ScreenTail.UI")]
    public void BothProcessesArePublishedTheSameWay(string project)
    {
        var csproj = File.ReadAllText(Repo.File("client", project, project + ".csproj"));

        Assert.Matches(new Regex(@"<Import Project=""\.\.\\Packaging\.props"" />"), csproj);
    }

    [Fact]
    public void TheInstalledBuildIsSelfContainedAndSingleFile()
    {
        // ADR-0003 left two gaps to packaging: the runtime settings that turn startup hooks off lived in a
        // file beside the executable, and the managed code beside a signed launcher was not signed. A
        // self-contained single-file build carries both inside the one signed executable.
        //
        // Native libraries are deliberately not packed in. A single-file build that carries them unpacks
        // them at run time into a folder under the user's profile, which the user can write to; left in
        // Program Files they sit where only an administrator can change them, and the signing step signs
        // each one.
        var installed = Packaged();

        Assert.Equal("win-x64", installed["RuntimeIdentifier"]);
        Assert.Equal("true", installed["SelfContained"]);
        Assert.Equal("true", installed["PublishSingleFile"]);
        Assert.Equal("false", installed["IncludeNativeLibrariesForSelfExtract"]);
        Assert.Equal("false", installed["IncludeAllContentForSelfExtract"]);
    }

    [Fact]
    public void TheInstalledServiceOpensNoWindow()
    {
        // The service starts at every sign-in. Built as a console program it would open a console window
        // on every technician's desktop, showing its log. Development builds stay console programs,
        // because that window is where a developer reads the log.
        var service = XDocument.Load(Repo.File("client", "ScreenTail.Service", "ScreenTail.Service.csproj"));

        var outputTypes = service.Descendants(MsBuild + "OutputType").ToList();
        var installed = Assert.Single(outputTypes, o => (string?)o.Attribute("Condition") == "'$(ScreenTailInstaller)' == 'true'");
        Assert.Equal("WinExe", installed.Value);
    }

    [Fact]
    public void TheUninstallModeRunsBeforeTheServiceTakesItsName()
    {
        // The uninstall runs the installed service with its own flag while a technician's copy may still be
        // running. It must neither wait for nor be refused by the single-instance mutex.
        var program = File.ReadAllText(Repo.File("client", "ScreenTail.Service", "Program.cs"));

        var mode = program.IndexOf("InstalledFootprint.UninstallFlag", StringComparison.Ordinal);
        var mutex = program.IndexOf("SingleInstance.TryAcquire", StringComparison.Ordinal);
        Assert.True(mode > 0 && mode < mutex, "the uninstall mode is dispatched before the single-instance mutex is taken");
    }

    [Fact]
    public void TheUninstallStepWaitsForTheInstalledFilesAfterEverythingElseItDoes()
    {
        // Windows Installer deletes the installed files the moment this step exits, and leaves one that
        // another program still has open for a restart: exit 3010 and the install folder left behind, as
        // on 2026-11-01 when the hosted runner's monitor was reading ScreenTail.Service.exe. The wait for
        // other programs to let go is the step's last act, after stopping the running copies (whose own
        // holds it then waits out) and after everything else that touches a file.
        var run = Method(Source("UninstallCleanup.cs"), "public static int Run(");

        var wait = run.IndexOf("ReleaseInstalledFiles(installDirectory, started)", StringComparison.Ordinal);
        Assert.True(wait > 0, "the uninstall step waits for the installed files");
        foreach (var before in new[] { "StopInstalledCopies(", "RemoveData(", "RemoveRunValues(" })
        {
            var at = run.IndexOf(before, StringComparison.Ordinal);
            Assert.True(at > 0 && at < wait, $"{before} comes before the wait");
        }

        var started = run.IndexOf("var started = TimeProvider.System.GetTimestamp();", StringComparison.Ordinal);
        Assert.True(started > 0 && started < run.IndexOf("StopInstalledCopies(", StringComparison.Ordinal), "the step's clock starts before it stops anything");
    }

    [Fact]
    public void TheWaitAsksWindowsAboutEveryInstalledFileWithinTheStepsDeadline()
    {
        // Shared by the uninstall step and the upgrade step. Asked of Windows, about every installed file,
        // for any process but this one, two clean looks apart; bounded by what is left of the step's own
        // deadline, measured from the step's start, so the step as a whole ends inside WixQuietExec's minute.
        var cleanup = Source("UninstallCleanup.cs");
        var at = cleanup.IndexOf("internal static ProgramFilesRelease.Outcome ReleaseInstalledFiles(string installDirectory, long started) =>", StringComparison.Ordinal);
        Assert.True(at > 0, "the shared wait is in UninstallCleanup");
        var call = cleanup[at..cleanup.IndexOf(';', at)];

        Assert.Contains("ProgramFilesRelease.WaitForOthers(", call, StringComparison.Ordinal);
        Assert.Contains("InstalledFiles(installDirectory)", call, StringComparison.Ordinal);
        Assert.Contains("FileHolders.Of", call, StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessId", call, StringComparison.Ordinal);
        Assert.Contains("ProgramFilesRelease.ConfirmAfter", call, StringComparison.Ordinal);
        Assert.Contains("ProgramFilesRelease.BudgetLeft(TimeProvider.System.GetElapsedTime(started))", call, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpgradeWaitsForTheOldVersionsFilesBeforeRemovingThem()
    {
        // A major upgrade removes the old version before installing the new one, and that removal never
        // runs the uninstall step. Without a wait of its own, a scanner still reading the service's
        // executable a second after Restart Manager ended it turns the upgrade into 3010 (ST-112, #247).
        // The version being removed runs its own upgrade mode straight before its files go, as the system
        // account, and only when an upgrade is what removes it.
        const string OnlyAnUpgradesRemoval = "REMOVE~=\"ALL\" AND UPGRADINGPRODUCTCODE";
        var action = Package().Descendants(Wix + "CustomAction").Single(a => (string?)a.Attribute("Id") == "ReleaseForUpgrade");
        var command = Package().Descendants(Wix + "SetProperty").Single(p => (string?)p.Attribute("Id") == "ReleaseForUpgrade");
        var scheduled = Package().Descendants(Wix + "InstallExecuteSequence").Elements(Wix + "Custom").Single(c => (string?)c.Attribute("Action") == "ReleaseForUpgrade");

        Assert.Equal($"\"[INSTALLFOLDER]{InstalledFootprint.ServiceExecutable}\" {InstalledFootprint.UpgradeReleaseFlag}", (string?)command.Attribute("Value"));
        Assert.Equal("execute", (string?)command.Attribute("Sequence"));
        Assert.Equal(OnlyAnUpgradesRemoval, (string?)command.Attribute("Condition"));
        Assert.Equal("RemoveFiles", (string?)scheduled.Attribute("Before"));
        Assert.Equal(OnlyAnUpgradesRemoval, (string?)scheduled.Attribute("Condition"));

        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("no", (string?)action.Attribute("Impersonate"));
        Assert.Equal("WixQuietExec", (string?)action.Attribute("DllEntry"));

        // Ignored: a file still held costs a restart, never the upgrade itself.
        Assert.Equal("ignore", (string?)action.Attribute("Return"));

        // Never the uninstall mode, which would erase every technician's data during the upgrade.
        Assert.DoesNotContain(InstalledFootprint.UninstallFlag, (string)command.Attribute("Value")!, StringComparison.Ordinal);
        Assert.DoesNotContain(InstalledFootprint.UpgradeReleaseFlag, InstalledFootprint.UninstallFlag, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpgradeModeStopsTheInstalledCopiesThenWaitsAndDoesNothingElse()
    {
        // It runs as the system account during every upgrade, so what it does is held here: refuse anybody
        // but the installer, stop the copies running from the install folder, wait, and report. Never a
        // user's data, a Run value or the service itself.
        var release = Source("UpgradeRelease.cs");
        var outer = Method(release, "public static int Run(TextWriter output)");
        var clock = outer.IndexOf("var started = TimeProvider.System.GetTimestamp();", StringComparison.Ordinal);
        var check = outer.IndexOf("UninstallCleanup.RunByTheInstaller()", StringComparison.Ordinal);
        var step = outer.IndexOf("Run(output, AppContext.BaseDirectory, started)", StringComparison.Ordinal);
        Assert.True(clock > 0 && clock < check, "the step's clock starts first");
        Assert.True(check > 0 && check < step, "the upgrade mode refuses anybody but the installer before it does anything");

        var body = Method(release, "internal static int Run(TextWriter output, string installDirectory, long started)");
        var stop = body.IndexOf("UninstallCleanup.StopInstalledCopies(installDirectory)", StringComparison.Ordinal);
        var wait = body.IndexOf("UninstallCleanup.ReleaseInstalledFiles(installDirectory, started)", StringComparison.Ordinal);
        Assert.True(stop > 0 && stop < wait, "it stops the installed copies, then waits for the files");

        var code = release[release.IndexOf("internal static class UpgradeRelease", StringComparison.Ordinal)..];
        foreach (var never in new[] { "RemoveData", "RemoveRunValues", "Shred", "Registry", "Delete", "Host", "Capture" })
        {
            Assert.DoesNotContain(never, code, StringComparison.Ordinal);
        }

        // Dispatched before the single-instance mutex and before the service is built, like the uninstall mode.
        var program = File.ReadAllText(Repo.File("client", "ScreenTail.Service", "Program.cs"));
        var mode = program.IndexOf("InstalledFootprint.UpgradeReleaseFlag", StringComparison.Ordinal);
        Assert.True(mode > 0 && mode < program.IndexOf("SingleInstance.TryAcquire", StringComparison.Ordinal), "the upgrade mode is dispatched before the mutex");
        Assert.True(mode < program.IndexOf("Host.CreateApplicationBuilder", StringComparison.Ordinal), "and before the service is built");
        Assert.Contains("return UpgradeRelease.Run(Console.Out);", program[mode..program.IndexOf("SingleInstance.TryAcquire", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    private static string Source(string file) => File.ReadAllText(Repo.File("client", "ScreenTail.Service", "Install", file));

    /// <summary>A block-bodied method's text, from its signature to its closing brace at member indentation.</summary>
    private static string Method(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{signature} is in the source");
        var body = source[at..];
        return body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];
    }

    /// <summary>The custom action whose command line (set, for a deferred action, by a property of its name) is the uninstall mode.</summary>
    private static XElement RemovalAction()
    {
        var command = Package().Descendants(Wix + "SetProperty")
            .Single(p => ((string?)p.Attribute("Value"))?.Contains(InstalledFootprint.UninstallFlag, StringComparison.Ordinal) == true);
        return Package().Descendants(Wix + "CustomAction").Single(a => (string?)a.Attribute("Id") == (string?)command.Attribute("Id"));
    }

    private static XDocument Package() => XDocument.Load(Repo.File("installer", "Package.wxs"));

    private static XDocument Bundle() => XDocument.Load(Repo.File("installer", "Bundle.wxs"));

    /// <summary>The properties Packaging.props sets when the installer build asks for them.</summary>
    private static Dictionary<string, string> Packaged()
    {
        var props = XDocument.Load(Repo.File("client", "Packaging.props"));
        var group = Assert.Single(props.Descendants(MsBuild + "PropertyGroup"), g => (string?)g.Attribute("Condition") == "'$(ScreenTailInstaller)' == 'true'");
        return group.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal);
    }

    /// <summary>A file in the repository, found from the test binary the way ClientSources finds projects.</summary>
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
