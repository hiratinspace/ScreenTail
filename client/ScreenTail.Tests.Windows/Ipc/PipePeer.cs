using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>
    /// Leaves this account only the right to ask what the peer is and to wait for it.
    ///
    /// This is what tells the kernel's record of a process from <c>Process.MainModule</c>, which has to
    /// read the peer's memory: with the peer's DACL cut down to PROCESS_QUERY_LIMITED_INFORMATION and
    /// SYNCHRONIZE, the first still answers and the second is refused. The test keeps the handle the
    /// peer was created with, which was granted before the DACL changed, so it can still end the process.
    ///
    /// Call it once the peer has shown it is running (a pipe peer has connected), so the change cannot
    /// land in the middle of PowerShell starting up and end it for a reason that is not the test's.
    ///
    /// Returns whether the restriction holds. An account with an enabled debug privilege walks past a
    /// DACL; on such a machine a revert to MainModule cannot be caught this way, and the test records
    /// that in the run's measurements rather than passing as though it could.
    /// </summary>
    public bool RestrictToQueryLimited()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var descriptor = new RawSecurityDescriptor(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"D:P(A;;0x{ProcessQueryLimitedInformation | Synchronize:X};;;{me.Value})"));
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        Assert.True(
            SetKernelObjectSecurity(_process.SafeHandle, DaclSecurityInformation, bytes),
            $"SetKernelObjectSecurity failed: {Marshal.GetLastPInvokeError()}");

        using var wide = OpenProcess(ProcessQueryInformation | ProcessVmRead, inheritHandle: false, (uint)_process.Id);
        return wide.IsInvalid;
    }

    /// <summary>
    /// Fails, naming the cause, when the peer is gone. Asked after the checks it was needed for: still
    /// running then means it was running throughout, so a refusal cannot be a process that had exited.
    /// </summary>
    public void AssertStillRunning() =>
        Assert.False(_process.HasExited, $"The PowerShell peer exited early (code {(_process.HasExited ? _process.ExitCode : 0)}), so this run says nothing about the code under test.");

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

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint DaclSecurityInformation = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle handle, uint securityInformation, byte[] securityDescriptor);

    /// <summary>
    /// The pipe names here are made by the tests from a GUID, but they go inside a quoted PowerShell
    /// string, so anything that could end the quote is refused rather than trusted.
    /// </summary>
    private static string Checked(string pipeName) =>
        pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? pipeName
            : throw new ArgumentException("A test pipe name is letters, digits, '-', '_' and '.' only.", nameof(pipeName));
}
