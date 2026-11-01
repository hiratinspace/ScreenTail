using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ScreenTail.Service.Install;

/// <summary>
/// Which processes have a file open, asked of the kernel (ST-112): the uninstall step's last act waits
/// until nobody but itself does (<see cref="Core.Install.ProgramFilesRelease"/>).
///
/// The file is opened for its attributes only, sharing everything, so asking never stands in the way of
/// the delete it is waiting for; NtQueryInformationFile's FileProcessIdsUsingFileInformation then lists
/// every process with a handle to it or a view of it, which is the list Restart Manager reads too.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FileHolders
{
    private const uint ReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const int FileProcessIdsUsingFileInformation = 47;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>The ids of the processes that have the file open; null when it cannot be asked about.</summary>
    public static IReadOnlyCollection<long>? Of(string path)
    {
        using var file = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (file.IsInvalid)
        {
            return null;
        }

        // A count and then one id per process; grown and asked again if more processes hold it than fit.
        for (var size = 4096; size <= 1 << 20; size *= 4)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationFile(file, out _, buffer, size, FileProcessIdsUsingFileInformation);
                if (status is StatusBufferOverflow or StatusInfoLengthMismatch)
                {
                    continue;
                }

                if (status != 0)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(buffer);
                var ids = new long[count];
                for (var i = 0; i < count; i++)
                {
                    ids[i] = Marshal.ReadIntPtr(buffer, IntPtr.Size + (i * IntPtr.Size)).ToInt64();
                }

                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, IntPtr information, int length, int informationClass);
}
