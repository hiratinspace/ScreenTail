using System.Runtime.Versioning;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Platform.Lifecycle;

namespace ScreenTail.Service.Host;

/// <summary>
/// Registers the running service with Windows for a restart by Restart Manager after another program's
/// installation closed it (ST-112). What and why is <see cref="ApplicationRestart"/>, in Core; the call is
/// <see cref="RestartRegistration"/>, shared with the UI.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsApplicationRestart
{
    /// <summary>Registers this process. Returns the HRESULT, 0 when Windows took it, and logs a refusal.</summary>
    public static int Register(ILogger logger)
    {
        var result = RestartRegistration.Register();
        if (result != 0)
        {
            LogRefused(logger, result);
        }

        return result;
    }

    /// <summary>Takes this process's registration back. For the Windows tests, which register the test host.</summary>
    internal static int Unregister() => RestartRegistration.Unregister();

    /// <summary>What Windows holds for this process, or null when nothing is registered.</summary>
    internal static (string CommandLine, int Flags)? Registered() => RestartRegistration.Registered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering for restart after an installation failed with 0x{Result:X8}; another program's installer that closes the service will not start it again")]
    private static partial void LogRefused(ILogger logger, int result);
}
