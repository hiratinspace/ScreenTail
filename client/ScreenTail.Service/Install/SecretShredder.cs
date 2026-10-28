using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Core.Install;

namespace ScreenTail.Service.Install;

/// <summary>
/// Removes the store's key and the device's credentials from the data folder of a user who is not
/// signed in, as the system account, one file at a time (ST-112, ADR-0007).
///
/// Nothing here works from a path below the profile folder. The profile folder is opened without
/// following a link and checked through its handle; each folder below it, and then each file, is opened
/// by its single name relative to the handle of the folder above, again without following a link, and
/// checked through its own handle: not a link, the kind expected, on the same volume, and for a file a
/// single name. Only then is the file marked for deletion, through that same handle. Whatever is renamed
/// or replaced while this runs, every handle names the object that was checked. Folders are never
/// deleted, and anything in doubt is left and counted.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SecretShredder
{
    internal enum Outcome
    {
        Removed,
        Absent,
        LeftInDoubt,
    }

    /// <summary>
    /// An outcome and, when it is in doubt, a short token saying why (an NTSTATUS, a Win32 error, or which
    /// check refused it). Numbers and fixed words only, never a path or a name (INV-10).
    /// </summary>
    internal readonly record struct Result(Outcome Outcome, string? Reason);

    /// <summary>One result per name in <see cref="InstalledFootprint.Secrets"/>, in its order.</summary>
    public static IReadOnlyList<Result> Shred(string profile)
    {
        var all = InstalledFootprint.Secrets.Count;
        using var root = OpenRoot(profile, out var rootError);
        if (root is null)
        {
            return Repeat(all, Outcome.LeftInDoubt, $"root-open:{rootError:x}");
        }

        if (!Describe(root, out var rootInfo, out var rootDescribe))
        {
            return Repeat(all, Outcome.LeftInDoubt, $"root-describe:{rootDescribe:x}");
        }

        if (!IsPlainDirectory(rootInfo))
        {
            return Repeat(all, Outcome.LeftInDoubt, "root-not-a-plain-directory");
        }

        var opened = new List<SafeFileHandle>();
        try
        {
            var parent = root;
            foreach (var segment in InstalledFootprint.DataSegments)
            {
                var status = OpenRelative(parent, segment, directory: true, out var child);
                if (status is StatusObjectNameNotFound or StatusObjectPathNotFound)
                {
                    return Repeat(all, Outcome.Absent, null);
                }

                if (child is null)
                {
                    return Repeat(all, Outcome.LeftInDoubt, $"dir-open:{status:x8}");
                }

                opened.Add(child);
                if (!Describe(child, out var info, out var describe))
                {
                    return Repeat(all, Outcome.LeftInDoubt, $"dir-describe:{describe:x}");
                }

                if (!IsPlainDirectory(info))
                {
                    return Repeat(all, Outcome.LeftInDoubt, "dir-not-a-plain-directory");
                }

                if (info.VolumeSerialNumber != rootInfo.VolumeSerialNumber)
                {
                    return Repeat(all, Outcome.LeftInDoubt, "dir-other-volume");
                }

                parent = child;
            }

            return [.. InstalledFootprint.Secrets.Select(name => ShredOne(parent, name, rootInfo.VolumeSerialNumber))];
        }
        finally
        {
            foreach (var handle in opened)
            {
                handle.Dispose();
            }
        }
    }

    private static List<Result> Repeat(int count, Outcome outcome, string? reason) =>
        [.. Enumerable.Repeat(new Result(outcome, reason), count)];

    private static Result ShredOne(SafeFileHandle folder, string name, uint volume)
    {
        var status = OpenRelative(folder, name, directory: false, out var file);
        if (status is StatusObjectNameNotFound or StatusObjectPathNotFound)
        {
            return new Result(Outcome.Absent, null);
        }

        if (file is null)
        {
            return new Result(Outcome.LeftInDoubt, $"open:{status:x8}");
        }

        using (file)
        {
            if (!Describe(file, out var info, out var describe))
            {
                return new Result(Outcome.LeftInDoubt, $"describe:{describe:x}");
            }

            if ((info.Attributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0)
            {
                return new Result(Outcome.LeftInDoubt, "link-or-directory");
            }

            if (info.NumberOfLinks != 1)
            {
                return new Result(Outcome.LeftInDoubt, "hard-linked");
            }

            if (info.VolumeSerialNumber != volume)
            {
                return new Result(Outcome.LeftInDoubt, "other-volume");
            }

            return MarkForDeletion(file, out var deleteError)
                ? new Result(Outcome.Removed, null)
                : new Result(Outcome.LeftInDoubt, $"delete:{deleteError}");
        }
    }

    /// <summary>The profile folder itself, opened without following it if it is a link.</summary>
    private static SafeFileHandle? OpenRoot(string profile, out int error)
    {
        var handle = CreateFileW(
            @"\\?\" + profile,
            FileTraverse | FileReadAttributes | Synchronize,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    /// <summary>
    /// One name, relative to an open folder, opening the link itself if the name is a link rather than
    /// following it (FILE_OPEN_REPARSE_POINT); the checks above then refuse it. Because the walk opens one
    /// component at a time this way, a link at any level is opened as a link and refused, so a link cannot
    /// redirect the step, and no whole path is ever resolved.
    /// </summary>
    private static int OpenRelative(SafeFileHandle parent, string name, bool directory, out SafeFileHandle? handle)
    {
        handle = null;
        var buffer = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            Marshal.StructureToPtr(
                new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = buffer },
                unicode,
                fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicode,
                Attributes = ObjCaseInsensitive,
            };

            var access = directory
                ? FileTraverse | FileReadAttributes | Synchronize
                : Delete | FileReadAttributes | Synchronize;
            var options = FileSynchronousIoNonAlert | FileOpenReparsePoint | (directory ? FileDirectoryFile : FileNonDirectoryFile);
            var status = NtCreateFile(out var opened, access, ref attributes, out _, IntPtr.Zero, 0, ShareAll, FileOpen, options, IntPtr.Zero, 0);
            if (status != 0 || opened.IsInvalid)
            {
                opened.Dispose();
                return status;
            }

            handle = opened;
            return 0;
        }
        finally
        {
            if (added)
            {
                parent.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool Describe(SafeFileHandle handle, out Description description, out int error)
    {
        description = default;
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var tag, (uint)Marshal.SizeOf<AttributeTagInfo>())
            || !GetFileInformationByHandle(handle, out var info))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        description = new Description(tag.FileAttributes, info.VolumeSerialNumber, info.NumberOfLinks);
        return true;
    }

    private static bool IsPlainDirectory(Description info) =>
        (info.Attributes & FileAttributeDirectory) != 0 && (info.Attributes & FileAttributeReparsePoint) == 0;

    /// <summary>Through the handle that was checked: the name goes when the handle closes, or at once.</summary>
    private static bool MarkForDeletion(SafeFileHandle file, out string error)
    {
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
        if (SetFileInformationByHandle(file, FileDispositionInfoEx, ref flags, sizeof(uint)))
        {
            error = "0";
            return true;
        }

        var ex = Marshal.GetLastWin32Error();

        // Older file systems take only the original form, which needs a writable file.
        byte delete = 1;
        if (SetFileInformationByHandle(file, FileDispositionInfo, ref delete, 1))
        {
            error = "0";
            return true;
        }

        error = $"{ex:x}/{Marshal.GetLastWin32Error():x}";
        return false;
    }

    private readonly record struct Description(uint Attributes, uint VolumeSerialNumber, uint NumberOfLinks);

    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);

    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileTraverse = 0x0020;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;

    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    private const int FileAttributeTagInfo = 9;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtCreateFile(
        out SafeFileHandle handle,
        uint access,
        ref ObjectAttributes attributes,
        out IoStatusBlock status,
        IntPtr allocationSize,
        uint fileAttributes,
        uint share,
        uint disposition,
        uint options,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint flags, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte delete, uint size);
}
