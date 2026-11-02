using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Platform.Lifecycle;

/// <summary>
/// Registers this process with Windows for Restart Manager's restart after another program's installer
/// closed it (ST-112), with the flags and command line of <see cref="ApplicationRestart"/>. Both processes
/// register: Windows Installer, told to ask only programs registered for restart, asks nobody while an
/// unregistered one holds an installed file, and the UI holds its own executable whenever it runs.
/// ScreenTail's own package turns the restart off (MSIDISABLERMRESTART).
/// </summary>
[SupportedOSPlatform("windows")]
public static class RestartRegistration
{
    /// <summary>Registers this process. Returns the HRESULT: 0 when Windows took it.</summary>
    public static int Register() =>
        RegisterApplicationRestart(ApplicationRestart.CommandLine.Length == 0 ? null : ApplicationRestart.CommandLine, ApplicationRestart.Flags);

    /// <summary>Takes this process's registration back. For the Windows tests, which register the test host.</summary>
    public static int Unregister() => UnregisterApplicationRestart();

    /// <summary>What Windows holds for this process: the command line and the flags, or null when nothing is registered.</summary>
    public static (string CommandLine, int Flags)? Registered()
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        if (GetApplicationRestartSettings(self.Handle, buffer, ref size, out var flags) != 0)
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
