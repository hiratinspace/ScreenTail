using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Platform.Lifecycle;

/// <summary>
/// Reads what <see cref="StartGuard"/> decides on, for either process (ST-112, INV-6): this token's
/// elevation, its session, and whether its user is the session's, compared by <see cref="SessionUser"/>
/// without any name lookup. Every read is local and bounded; nothing here asks a domain controller.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartFactsReader
{
    /// <summary>This process's facts. Waits at most about three seconds, and only while the session has no shell yet.</summary>
    public static StartFacts Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var session = ProcessIdToSessionId((uint)Environment.ProcessId, out var id) ? id : 0;
        var token = identity.Token;
        var match = SessionUser.Resolve(identity.User?.Value, ShellUserSid, () => LogonSessionName(token), () => SignedInName(session));
        return new StartFacts(ElevationOf(token), session, match);
    }

    /// <summary>A token's elevation type, or Unknown when it cannot be read.</summary>
    public static TokenElevation ElevationOf(IntPtr token)
    {
        var value = ReadInt32(token, TokenElevationTypeClass);
        return value is >= 1 and <= 3 ? (TokenElevation)value : TokenElevation.Unknown;
    }

    /// <summary>
    /// The elevated half of this token's split pair (TokenLinkedToken), for the Windows tests that exercise the
    /// Full branch on a machine with UAC on. Zero when there is none. The caller closes it.
    /// </summary>
    public static IntPtr LinkedToken(IntPtr token)
    {
        var size = IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return GetTokenInformation(token, TokenLinkedTokenClass, buffer, size, out _) ? Marshal.ReadIntPtr(buffer) : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Closes a handle <see cref="LinkedToken"/> returned.</summary>
    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>The SID of the user who owns this desktop's shell window, or null when there is none or its token cannot be read.</summary>
    public static string? ShellUserSid()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                return null;
            }

            try
            {
                using var shellIdentity = new WindowsIdentity(token);
                return shellIdentity.User?.Value;
            }
            catch (Exception ex) when (ex is ArgumentException or SecurityException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>The user and domain of a token's logon session, as LSA holds them locally, or null.</summary>
    public static AccountName? LogonSessionName(IntPtr token)
    {
        // TOKEN_STATISTICS: TokenId (LUID), then AuthenticationId (LUID), the logon session.
        var size = 64;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenStatisticsClass, buffer, size, out _))
            {
                return null;
            }

            var logon = Marshal.PtrToStructure<Luid>(buffer + 8);
            if (LsaGetLogonSessionData(ref logon, out var data) != 0 || data == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var session = Marshal.PtrToStructure<LogonSessionData>(data);
                var user = Read(session.UserName);
                return string.IsNullOrEmpty(user) ? null : new AccountName(user, Read(session.LogonDomain) ?? string.Empty);
            }
            finally
            {
                _ = LsaFreeReturnBuffer(data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The user and domain signed in to a session, as Windows reports them, or null when nobody is.</summary>
    public static AccountName? SignedInName(uint session)
    {
        var user = SessionString(session, WTSUserName);
        return string.IsNullOrEmpty(user) ? null : new AccountName(user, SessionString(session, WTSDomainName) ?? string.Empty);
    }

    private static string? Read(UnicodeString value) =>
        value.Buffer == IntPtr.Zero || value.Length == 0 ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);

    private static int ReadInt32(IntPtr token, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return GetTokenInformation(token, infoClass, buffer, sizeof(int), out _) ? Marshal.ReadInt32(buffer) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? SessionString(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private const int TokenStatisticsClass = 10;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>The start of SECURITY_LOGON_SESSION_DATA: only the fields read here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LogonSessionData
    {
        public uint Size;
        public Luid LogonId;
        public UnicodeString UserName;
        public UnicodeString LogonDomain;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("secur32.dll")]
    private static extern int LsaGetLogonSessionData(ref Luid logonId, out IntPtr data);

    [DllImport("secur32.dll")]
    private static extern int LsaFreeReturnBuffer(IntPtr buffer);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
