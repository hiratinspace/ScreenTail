using System.Diagnostics;
using System.Runtime.Versioning;
using ScreenTail.Core.Install;
using ScreenTail.Service.Install;

namespace ScreenTail.Tests.Windows.Install;

/// <summary>
/// The uninstall step's last act on a real file (ST-112): it asks Windows who has the installed files open
/// and waits until nobody but itself does, because Windows Installer deletes them the moment it exits.
///
/// On 2026-11-01 the hosted runner's own monitor (provjobd) read ScreenTail.Service.exe as the step ended,
/// without sharing deletion, and the uninstall finished with 3010 and the install folder left behind. The
/// other process here opens a file the same way, so a delete straight away fails with the same sharing
/// violation Windows Installer met, and the test proves the wait turns that into a delete that succeeds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileHoldersTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("screentail-holders-").FullName;

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
    public void AFileThisProcessHasOpenListsThisProcess()
    {
        var path = Path.Combine(_folder, "held.bin");
        File.WriteAllBytes(path, new byte[1024]);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Contains((long)Environment.ProcessId, FileHolders.Of(path) ?? []);
        }

        Assert.DoesNotContain((long)Environment.ProcessId, FileHolders.Of(path) ?? []);
    }

    [Fact]
    public void AFileThatIsNotThereCannotBeAskedAbout()
    {
        Assert.Null(FileHolders.Of(Path.Combine(_folder, "missing.bin")));
    }

    [Fact]
    public void TheWaitOutlastsAnotherProgramReadingTheFileSoTheDeleteSucceeds()
    {
        var path = Path.Combine(_folder, "ScreenTail.Service.exe");
        File.WriteAllBytes(path, new byte[64 * 1024]);
        var opened = Path.Combine(_folder, "opened");

        // Read access, read sharing only: no delete sharing, as the runner's monitor opened it. It holds
        // the file for a second and a half after saying it has it. The paths reach it through its
        // environment rather than spliced into the command, so no character in a temp path (an apostrophe
        // in a user name) can change what the command says.
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
            Assert.Contains((long)reader.Id, FileHolders.Of(path) ?? []);

            // The failure itself: while it reads, the file cannot be deleted.
            var refused = Assert.ThrowsAny<IOException>(() => File.Delete(path));
            Assert.Equal(unchecked((int)0x80070020), refused.HResult);

            var outcome = ProgramFilesRelease.WaitForOthers(
                [path],
                FileHolders.Of,
                Environment.ProcessId,
                TimeProvider.System,
                Thread.Sleep,
                ProgramFilesRelease.Budget,
                ProgramFilesRelease.PollEvery,
                ProgramFilesRelease.ConfirmAfter);

            Assert.Equal(0, outcome.StillHeld);
            Assert.True(outcome.Waited > TimeSpan.Zero, "the wait saw the other process holding the file");
            Assert.True(outcome.Waited < ProgramFilesRelease.Budget, $"the wait ended when the file was let go, after {outcome.Waited}");

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
        }
    }
}
