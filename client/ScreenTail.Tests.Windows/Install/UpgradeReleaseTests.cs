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
        var path = Installed(relative);

        // Read access, read sharing only: no delete sharing, as the runner's monitor opened it.
        using (Hold(path, "Read", 1500))
        {
            var refused = Assert.ThrowsAny<IOException>(() => File.Delete(path));
            Assert.Equal(unchecked((int)0x80070020), refused.HResult);

            using var output = new StringWriter();
            var code = UpgradeRelease.Run(output, _folder, TimeProvider.System.GetTimestamp());

            var (waited, stillOpen) = Report(output.ToString());
            Assert.Equal(0, stillOpen);
            Assert.Equal(0, code);
            Assert.True(waited > 0, $"the step saw the other process holding the file: {output}");
            Assert.DoesNotContain("still open when the wait ended", output.ToString(), StringComparison.Ordinal);

            // What Windows Installer does next.
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    [Theory]
    [InlineData("Read", "held without sharing deletion")]
    [InlineData("Read, Delete", "held, sharing deletion")]
    public void AWaitThatRunsOutNamesTheInstalledFilesStillOpenAndTheKindOfHold(string share, string kind)
    {
        // The case the log has to make diagnosable: the step's deadline is all but spent (it started 44 of
        // its 45 seconds ago), and another program holds a file for longer than the second that is left.
        // The line names the file below the install folder, never the folder itself, a process or an id.
        var path = Installed(@"runtimes\win-x64\whisper.dll");
        _ = Installed("ScreenTail.Service.exe");
        var started = TimeProvider.System.GetTimestamp() - (44 * TimeProvider.System.TimestampFrequency);

        using (Hold(path, share, 10000))
        {
            using var output = new StringWriter();
            var code = UpgradeRelease.Run(output, _folder, started);

            var (_, stillOpen) = Report(output.ToString());
            Assert.Equal(1, stillOpen);
            Assert.Equal(1, code);

            // The whole line, exactly: the file below the folder and the kind of hold, nothing else on it.
            var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal($@"{UpgradeRelease.Prefix} still open when the wait ended: runtimes\win-x64\whisper.dll ({kind}).", lines[2]);
            Assert.DoesNotContain(_folder, output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private string Installed(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[64 * 1024]);
        return path;
    }

    /// <summary>
    /// Another process opens the file for reading with the given sharing and holds it, and this returns once
    /// it has it; disposing ends it. The path reaches it through its environment, not spliced into its
    /// command, so no character in a temp path can change what the command says.
    /// </summary>
    private static Holder Hold(string path, string share, int milliseconds)
    {
        var opened = Path.Combine(Path.GetTempPath(), $"screentail-upgrade-opened-{Guid.NewGuid():N}");
        var start = new ProcessStartInfo(PowerShell)
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-Command",
                "$f = [IO.File]::Open($env:ST_HOLD_PATH, 'Open', 'Read', $env:ST_HOLD_SHARE); [IO.File]::WriteAllText($env:ST_HOLD_OPENED, 'yes'); Start-Sleep -Milliseconds ([int]$env:ST_HOLD_MS); $f.Dispose()",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["ST_HOLD_PATH"] = path;
        start.Environment["ST_HOLD_OPENED"] = opened;
        start.Environment["ST_HOLD_SHARE"] = share;
        start.Environment["ST_HOLD_MS"] = milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = new Holder(Process.Start(start)!, opened);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(opened) && deadline.Elapsed < TimeSpan.FromSeconds(30) && !holder.Process.HasExited)
        {
            Thread.Sleep(20);
        }

        if (!File.Exists(opened))
        {
            holder.Dispose();
            Assert.Fail("the other process opened the file");
        }

        return holder;
    }

    private sealed class Holder(Process process, string opened) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
            }

            Process.WaitForExit();
            Process.Dispose();
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
