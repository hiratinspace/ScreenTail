using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Service.Host;

/// <summary>
/// The capture service's check of itself at every start (ST-112, INV-6): the rule is <see cref="StartGuard"/>
/// in Core, and this reads its inputs from the process token and the session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceStart
{
    /// <summary>
    /// Says why and returns the exit code (0) when this process may not be the service; null to go on.
    /// Program.cs calls it right after the single-instance name and before the host is built.
    /// </summary>
    public static int? RefuseIfUnfit(StartFacts facts, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var refusal = StartGuard.Decide(facts);
        if (refusal == StartRefusal.None)
        {
            return null;
        }

        error.WriteLine(StartGuard.Explain(refusal));
        return 0;
    }

    /// <summary>This process's facts: its token's elevation and user, its session, and that session's user.</summary>
    public static StartFacts Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var session = SessionOf(Environment.ProcessId);
        return new StartFacts(ElevationOf(identity.Token), session, identity.User?.Value, SessionUser(session));
    }

    private static uint SessionOf(int processId) =>
        ProcessIdToSessionId((uint)processId, out var session) ? session : 0;

    private static TokenElevation ElevationOf(IntPtr token)
    {
        var size = Marshal.SizeOf<int>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenElevationTypeClass, buffer, size, out _))
            {
                return TokenElevation.Unknown;
            }

            var value = Marshal.ReadInt32(buffer);
            return value is >= 1 and <= 3 ? (TokenElevation)value : TokenElevation.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The SID of the user signed in to the session, or null when nobody is or it cannot be read.</summary>
    private static string? SessionUser(uint session)
    {
        var user = SessionString(session, WTSUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var domain = SessionString(session, WTSDomainName);
        try
        {
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            return account.Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
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

    private const int TokenElevationTypeClass = 18;
    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
