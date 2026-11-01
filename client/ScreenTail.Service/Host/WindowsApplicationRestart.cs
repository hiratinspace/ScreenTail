using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Service.Host;

/// <summary>
/// Registers the running service with Windows for a restart by Restart Manager after another program's
/// installation closed it (ST-112). What and why is <see cref="ApplicationRestart"/>, in Core.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsApplicationRestart
{
    /// <summary>Registers this process. Returns the HRESULT, 0 when Windows took it, and logs a refusal.</summary>
    public static int Register(ILogger logger)
    {
        var result = RegisterApplicationRestart(ApplicationRestart.CommandLine.Length == 0 ? null : ApplicationRestart.CommandLine, ApplicationRestart.Flags);
        if (result != 0)
        {
            LogRefused(logger, result);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering for restart after an installation failed with 0x{Result:X8}; another program's installer that closes the service will not start it again")]
    private static partial void LogRefused(ILogger logger, int result);

    /// <summary>Takes this process's registration back. For the Windows tests, which register the test host.</summary>
    internal static int Unregister() => UnregisterApplicationRestart();

    /// <summary>What Windows holds for this process: the command line and the flags, or null when nothing is registered.</summary>
    internal static (string CommandLine, int Flags)? Registered()
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var result = GetApplicationRestartSettings(self.Handle, buffer, ref size, out var flags);
        if (result != 0)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, '\0');
        return (new string(buffer, 0, length < 0 ? 0 : length), (int)flags);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationRestartSettings(IntPtr process, [Out] char[] commandLine, ref uint size, out uint flags);
}
