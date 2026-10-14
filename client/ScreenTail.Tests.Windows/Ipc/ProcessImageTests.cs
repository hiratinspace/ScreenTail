using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Platform.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// Which file a process was started from, as both verifiers ask it (2026-10-12 review, T-80).
///
/// ProcessImage replaced <c>Process.MainModule</c>, which reads the answer out of the peer's own memory,
/// where a same-user process can rewrite it. Nothing tested the replacement, so a revert would have
/// passed: for an ordinary process both give the same path. What tells them apart without imitating the
/// attack is the access each needs. MainModule has to read the peer's memory; the kernel's record needs
/// only the right to ask what a process is. A peer that grants nothing more is named by one and not the
/// other.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessImageTests
{
    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ProcessImageNamesTheRunningFile()
    {
        using var image = ProcessImage.Of((uint)Environment.ProcessId);

        Assert.NotNull(image);
        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), image.Path, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ProcessImageNamesAnotherProcessAndNotThisOne()
    {
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        using var peer = PipePeer.Idling();

        using var image = ProcessImage.Of((uint)peer.Id);

        Assert.NotNull(image);
        Assert.Equal(PipePeer.Executable, image.Path, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ProcessImageNeedsOnlyTheRightToAskWhatAProcessIs()
    {
        Assert.SkipUnless(PipePeer.Available, "Windows PowerShell not found");
        using var peer = PipePeer.Idling();

        // The peer's DACL now lets this account ask what it is and wait for it, and nothing else. The
        // test keeps the handle it was started with, so it can still end the process afterwards.
        GrantOnlyQueryLimited(peer.Process.SafeHandle);

        using (var wide = OpenProcess(ProcessQueryInformation | ProcessVmRead, inheritHandle: false, (uint)peer.Id))
        {
            // Whether the restriction took. An account holding an enabled debug privilege walks past a
            // DACL, and on such a machine this test cannot tell the two readings apart; it says so in the
            // run's measurements instead of passing as though it could.
            if (!wide.IsInvalid)
            {
                Measurements.Record("ProcessImageNeedsOnlyTheRightToAskWhatAProcessIs: the restricted DACL did not hold on this machine, so a revert to MainModule would not be caught here");
            }
        }

        using var image = ProcessImage.Of((uint)peer.Id);

        Assert.NotNull(image);
        Assert.Equal(PipePeer.Executable, image.Path, ignoreCase: true);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AProcessIdThatNamesNothingHasNoImage()
    {
        // "Cannot tell" is a refusal, never a path: both verifiers turn null into a reason.
        Assert.Null(ProcessImage.Of(0));
    }

    private static void GrantOnlyQueryLimited(SafeProcessHandle process)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var descriptor = new RawSecurityDescriptor(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"D:P(A;;0x{ProcessQueryLimitedInformation | Synchronize:X};;;{me.Value})"));
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        Assert.True(
            SetKernelObjectSecurity(process, DaclSecurityInformation, bytes),
            $"SetKernelObjectSecurity failed: {Marshal.GetLastPInvokeError()}");
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
}
