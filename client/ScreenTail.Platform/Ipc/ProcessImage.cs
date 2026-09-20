using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ScreenTail.Platform.Ipc;

/// <summary>
/// Which file a running process was started from, asked of the kernel (ST-012).
///
/// <b>Not <c>Process.MainModule.FileName</c>.</b> That reads the target process's own loader data,
/// which lives in that process's memory and which a process running as the same user can rewrite. The
/// peer check is the one thing standing between the capture service and a same-user impostor, and it was
/// asking the impostor to describe itself: overwrite the path in your own PEB, name the real signed
/// executable, and the verifier goes off and checks that genuine file on disk (2026-09-19 review).
///
/// <c>QueryFullProcessImageName</c> answers from the kernel's own record of what was mapped, which the
/// process cannot reach.
///
/// <b>The handle is the other half.</b> A process id is reused the moment the process it named exits, so
/// between reading the id off the pipe and looking it up there is a window where the answer describes
/// somebody else entirely. Holding an open handle across the check pins the process object: the id
/// cannot be handed to anything new while we are still holding it, so the file we verify and the peer we
/// are talking to are the same thing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessImage : IDisposable
{
    /// <summary>
    /// Enough to ask a process what it is, and nothing else.
    ///
    /// PROCESS_QUERY_LIMITED_INFORMATION rather than PROCESS_QUERY_INFORMATION: it is granted across
    /// integrity levels and asks for no right to read memory, write memory or change anything. A
    /// verifier that needed more access than it uses would be a verifier worth attacking.
    /// </summary>
    private const int QueryLimitedInformation = 0x1000;

    private readonly SafeProcessHandle _handle;

    private ProcessImage(SafeProcessHandle handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The full path of the file the process was started from.</summary>
    public string Path { get; }

    /// <summary>
    /// Opens the process and reads its image path, keeping it open until this is disposed.
    ///
    /// Null when the process is gone, or when this account may not ask — both of which are refusals
    /// rather than passes. "Cannot tell" is not "yes".
    /// </summary>
    public static ProcessImage? Of(uint processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, inheritHandle: false, processId);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        // The documented maximum path, doubled: long paths are enabled per-machine, and a truncated
        // answer would be a different file that happens to start the same way.
        var buffer = new char[32_768];
        var length = buffer.Length;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
        {
            handle.Dispose();
            return null;
        }

        return new ProcessImage(handle, new string(buffer, 0, length));
    }

    /// <summary>Lets go of the process. Nothing may be trusted about <see cref="Path"/> after this.</summary>
    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        int desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        int flags,
        [Out] char[] exeName,
        ref int size);
}
