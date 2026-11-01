using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ScreenTail.Service.Install;

namespace ScreenTail.Tests.Windows.Install;

/// <summary>
/// The installer's step when an upgrade removes this version (ST-112), on real files: another process
/// reads one of the installed files without sharing deletion, as the hosted runner's monitor and virus
/// scanners do, and the step must not exit until it has let go, because Windows Installer deletes the
/// files the moment it does. The installer's smoke test proves the same through a real upgrade on CI.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class UpgradeReleaseTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("screentail-upgrade-").FullName;

    private static string PowerShell { get; } = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the temp folder is the runner's to clear.
        }
    }

    [Fact]
    public void NothingHeldIsNoWaitAndNothingLeftOpen()
    {
        File.WriteAllBytes(Path.Combine(_folder, "ScreenTail.Service.exe"), new byte[1024]);
        using var output = new StringWriter();

        var code = UpgradeRelease.Run(output, _folder, TimeProvider.System.GetTimestamp());

        Assert.Equal(0, code);
        var (waited, stillOpen) = Report(output.ToString());
        Assert.Equal(0, waited);
        Assert.Equal(0, stillOpen);
        Assert.Contains($"{UpgradeRelease.Prefix} 0 running copies stopped.", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ScreenTail.Service.exe")]
    [InlineData(@"runtimes\win-x64\whisper.dll")]
    public void TheStepOutlastsAnotherProgramReadingAnInstalledFileSoItCanBeDeleted(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64 * 1024]);
        var opened = Path.Combine(Path.GetTempPath(), $"screentail-upgrade-opened-{Guid.NewGuid():N}");

        // Read access, read sharing only: no delete sharing, as the runner's monitor opened it. The paths
        // reach the other process through its environment, not spliced into its command.
        var start = new ProcessStartInfo(PowerShell)
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-Command",
                "$f = [IO.File]::Open($env:ST_HOLD_PATH, 'Open', 'Read', 'Read'); [IO.File]::WriteAllText($env:ST_HOLD_OPENED, 'yes'); Start-Sleep -Milliseconds 1500; $f.Dispose()",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["ST_HOLD_PATH"] = path;
        start.Environment["ST_HOLD_OPENED"] = opened;
        using var reader = Process.Start(start)!;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(opened) && deadline.Elapsed < TimeSpan.FromSeconds(30) && !reader.HasExited)
            {
                Thread.Sleep(20);
            }

            Assert.True(File.Exists(opened), "the other process opened the file");
            var refused = Assert.ThrowsAny<IOException>(() => File.Delete(path));
            Assert.Equal(unchecked((int)0x80070020), refused.HResult);

            using var output = new StringWriter();
            var code = UpgradeRelease.Run(output, _folder, TimeProvider.System.GetTimestamp());

            var (waited, stillOpen) = Report(output.ToString());
            Assert.Equal(0, stillOpen);
            Assert.Equal(0, code);
            Assert.True(waited > 0, $"the step saw the other process holding the file: {output}");

            // What Windows Installer does next.
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (!reader.HasExited)
            {
                reader.Kill();
            }

            reader.WaitForExit();
            File.Delete(opened);
        }
    }

    /// <summary>The wait's line, as the installer's log and the smoke test read it.</summary>
    private static (long Waited, int StillOpen) Report(string output)
    {
        var match = WaitLine().Match(output);
        Assert.True(match.Success, $"the step reported its wait: {output}");
        return (long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"ScreenTail upgrade: waited (\d+) ms for other programs to close the installed files; (\d+) still open\.")]
    private static partial Regex WaitLine();
}
