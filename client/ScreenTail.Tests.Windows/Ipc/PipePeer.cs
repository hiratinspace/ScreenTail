using System.Diagnostics;
using System.Runtime.Versioning;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// Another process on the far end of a pipe, started from a directory that is not this test's.
///
/// The verifiers ask Windows which process is on the other end, and a test whose two ends are both this
/// process cannot tell "the peer" from "ourselves": ask for the client's id where the server's was wanted
/// and the answer is still this process, still in this directory, still accepted. A separate process
/// from somewhere else is the only peer that makes the wrong question give the wrong answer
/// (2026-10-12 review, T-80).
///
/// Windows PowerShell, because it ships with every Windows this runs on, lives in System32 rather than
/// beside the tests, and can open a named pipe from an inline command with nothing written to disk. The
/// inline command is allowed under the Restricted execution policy the laptop's runner account has, which
/// blocks script files, not commands.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PipePeer : IDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _grace;

    private PipePeer(Process process, TimeSpan grace)
    {
        _process = process;
        _grace = grace;
    }

    /// <summary>The executable the peer was started from, as the test knows it independently of any verifier.</summary>
    public static string Executable { get; } = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static bool Available => File.Exists(Executable);

    public int Id => _process.Id;

    /// <summary>The process itself, for a test that needs its handle.</summary>
    public Process Process => _process;

    /// <summary>A peer listening on <paramref name="pipeName"/>, holding the connection until the client lets go.</summary>
    public static PipePeer Serving(string pipeName) => Start(
        $"$p = New-Object System.IO.Pipes.NamedPipeServerStream('{Checked(pipeName)}', [System.IO.Pipes.PipeDirection]::InOut); "
        + "$p.WaitForConnection(); [void]$p.ReadByte(); $p.Dispose()",
        PipeGrace);

    /// <summary>A peer that connects to <paramref name="pipeName"/>, holding the connection until the server lets go.</summary>
    public static PipePeer Connecting(string pipeName) => Start(
        $"$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{Checked(pipeName)}', [System.IO.Pipes.PipeDirection]::InOut); "
        + "$p.Connect(30000); [void]$p.ReadByte(); $p.Dispose()",
        PipeGrace);

    /// <summary>A peer that does nothing but stay alive, for a test that only needs a process to ask about.</summary>
    public static PipePeer Idling() => Start("Start-Sleep -Seconds 60", TimeSpan.Zero);

    /// <summary>How long a pipe peer is given to notice the test's end has closed and leave by itself.</summary>
    private static readonly TimeSpan PipeGrace = TimeSpan.FromSeconds(5);

    public void Dispose()
    {
        try
        {
            // The pipe peers leave by themselves once the test's end closes; this is for the ones that
            // have not, and for the idler, which is given no time at all.
            if (!_process.WaitForExit(_grace))
            {
                _process.Kill();
                _ = _process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or going. Cleanup must not be the thing a test fails on.
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static PipePeer Start(string command, TimeSpan grace)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // Its own directory, not the test's, so the dev-build "same directory" rule cannot pass it
            // by accident.
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command })
        {
            start.ArgumentList.Add(argument);
        }

        return new PipePeer(Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start."), grace);
    }

    /// <summary>
    /// The pipe names here are made by the tests from a GUID, but they go inside a quoted PowerShell
    /// string, so anything that could end the quote is refused rather than trusted.
    /// </summary>
    private static string Checked(string pipeName) =>
        pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? pipeName
            : throw new ArgumentException("A test pipe name is letters, digits, '-', '_' and '.' only.", nameof(pipeName));
}
